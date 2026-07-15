using AutoChartSwitch.Core;
using Newtonsoft.Json.Linq;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using OBSWebsocketDotNet.Types;
using OBSWebsocketDotNet.Types.Events;

namespace AutoChartSwitch.Obs;

public sealed class ObsChartPublisher : IChartPublisher
{
    private const string RestartAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART";
    private readonly OBSWebsocket _client = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cycleLock = new();
    private string _url = "";
    private string _password = "";
    private bool _intentionalDisconnect;
    private bool _disposed;
    private long _generation;
    private PlaybackCycle? _cycle;
    private string? _pendingExitScene;

    public bool IsConnected => _client.IsConnected;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<bool>? ConnectionChanged;

    public ObsChartPublisher()
    {
        _client.Connected += OnConnected;
        _client.Disconnected += OnDisconnected;
        _client.MediaInputPlaybackStarted += OnPlaybackStarted;
        _client.MediaInputPlaybackEnded += OnPlaybackEnded;
    }

    public async Task ConnectAsync(string url, string password, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "ws")
            throw new ArgumentException("OBS URL must be an absolute ws:// URL.", nameof(url));

        _url = url;
        _password = password;
        _intentionalDisconnect = false;
        if (_client.IsConnected) _client.Disconnect();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => connected.TrySetResult();
        _client.Connected += handler;
        try
        {
            _client.ConnectAsync(url, password);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        }
        finally { _client.Connected -= handler; }
    }

    public Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
        if (_client.IsConnected) _client.Disconnect();
        ConnectionChanged?.Invoke(this, false);
        return Task.CompletedTask;
    }

    public async Task<ObsDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        return await WithClientAsync(() =>
        {
            var inputs = _client.GetInputList()
                .Select(ToInputInfo)
                .Where(x => x is not null)
                .Cast<ObsInputInfo>()
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var scenes = _client.ListScenes().Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return new ObsDiscovery(inputs, scenes);
        }, cancellationToken);
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected) return PublishResult.Failure("OBS is not connected.");
        await _requestGate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => PublishCoreAsync(request, cancellationToken), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return PublishResult.Failure($"OBS publish failed: {Friendly(ex)}"); }
        finally { _requestGate.Release(); }
    }

    public async Task<PublishResult> RetryExitSceneAsync(CancellationToken cancellationToken = default)
    {
        string? scene;
        lock (_cycleLock) scene = _pendingExitScene;
        if (string.IsNullOrWhiteSpace(scene)) return PublishResult.Failure("There is no failed exit-scene switch to retry.");
        if (!_client.IsConnected) return PublishResult.Failure("OBS is not connected.");
        try
        {
            await WithClientAsync(() => { _client.SetCurrentProgramScene(scene); return true; }, cancellationToken);
            lock (_cycleLock) _pendingExitScene = null;
            RaiseStatus($"Switched to exit scene '{scene}'.");
            return PublishResult.Success("Exit scene switch completed.");
        }
        catch (Exception ex)
        {
            var message = $"Exit scene switch failed: {Friendly(ex)}";
            RaiseStatus(message);
            return PublishResult.Failure(message);
        }
    }

    private async Task<PublishResult> PublishCoreAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Settings.TransitionToSourceDelayMilliseconds is < 0 or > 60000)
            return PublishResult.Failure("Transition-to-source delay must be from 0 to 60000 milliseconds.");
        var discovery = DiscoverCore();
        var mapping = request.Settings.Mappings.AsDictionary();
        var mappingError = ValidateMappings(mapping, discovery.Inputs);
        if (mappingError is not null) return PublishResult.Failure(mappingError);

        if (request.Mode == PublishMode.Pop && request.Settings.AutoSwitch)
        {
            if (string.IsNullOrWhiteSpace(request.Settings.EntryScene) || string.IsNullOrWhiteSpace(request.Settings.ExitScene))
                return PublishResult.Failure("Entry and exit scenes are required when Auto-switch is enabled.");
            if (!discovery.Scenes.Contains(request.Settings.EntryScene, StringComparer.Ordinal) ||
                !discovery.Scenes.Contains(request.Settings.ExitScene, StringComparer.Ordinal))
                return PublishResult.Failure("The configured entry or exit scene no longer exists in OBS.");
            var showcaseSettings = _client.GetInputSettings(mapping[ObsOutput.ShowcaseVideo]).Settings;
            if (showcaseSettings.Value<bool?>("looping") == true || showcaseSettings.Value<bool?>("loop") == true)
                return PublishResult.Failure("Showcase Video looping must be disabled for Auto-switch.");
        }

        var currentValues = Project(request.Chart, request.DifficultyImagePath);
        var previousValues = request.PreviousChart is null
            ? null
            : Project(request.PreviousChart, ChartFormatter.GetDifficultyImagePath(request.Settings.DifficultyCustomPath, request.PreviousChart.DifficultyName));
        var outputs = SelectOutputs(request.Mode, currentValues, previousValues);
        var snapshots = outputs.ToDictionary(output => output, output => (JObject)_client.GetInputSettings(mapping[output]).Settings.DeepClone());
        var originalScene = request.Mode == PublishMode.Pop && request.Settings.AutoSwitch ? _client.GetCurrentProgramScene() : null;
        var modified = new List<ObsOutput>();
        var sceneChanged = false;
        PlaybackCycle? priorCycle;
        string? priorPendingExit;
        lock (_cycleLock)
        {
            priorCycle = _cycle;
            priorPendingExit = _pendingExitScene;
        }

        try
        {
            if (request.Mode == PublishMode.Pop && request.Settings.AutoSwitch)
            {
                _client.SetCurrentProgramScene(request.Settings.EntryScene);
                sceneChanged = true;

                if (request.Settings.TransitionToSourceDelayMilliseconds > 0)
                {
                    RaiseStatus($"Entry transition started. Waiting {request.Settings.TransitionToSourceDelayMilliseconds} ms before updating chart sources.");
                    await Task.Delay(request.Settings.TransitionToSourceDelayMilliseconds, cancellationToken);
                }
            }

            foreach (var output in outputs.Where(x => x is not ObsOutput.ShowcaseVideo))
            {
                SetValue(mapping[output], output, currentValues[output]);
                modified.Add(output);
            }

            if (outputs.Contains(ObsOutput.ShowcaseVideo))
            {
                SetValue(mapping[ObsOutput.ShowcaseVideo], ObsOutput.ShowcaseVideo, currentValues[ObsOutput.ShowcaseVideo]);
                modified.Add(ObsOutput.ShowcaseVideo);
            }

            var restartShowcase = request.Mode is PublishMode.Pop or PublishMode.Retry || outputs.Contains(ObsOutput.ShowcaseVideo);
            PlaybackCycle? existing;
            lock (_cycleLock) existing = _cycle;
            if (restartShowcase)
            {
                if (request.Mode == PublishMode.Pop)
                {
                    if (request.Settings.AutoSwitch)
                        ArmCycle(mapping[ObsOutput.ShowcaseVideo], request.Settings.ExitScene);
                    else
                        DisarmCycle();
                }
                else if (existing is not null)
                    ArmCycle(mapping[ObsOutput.ShowcaseVideo], existing.ExitScene);

                _client.TriggerMediaInputAction(mapping[ObsOutput.ShowcaseVideo], RestartAction);
                StartPlaybackPoll();
            }

            return PublishResult.Success(request.Mode switch
            {
                PublishMode.Pop => "Chart displayed and removed from the queue.",
                PublishMode.QuickEdit => "Current display updated.",
                _ => "Current display synchronized again."
            });
        }
        catch (Exception ex)
        {
            lock (_cycleLock)
            {
                _cycle = priorCycle;
                _pendingExitScene = priorPendingExit;
            }
            var rollbackErrors = new List<string>();
            foreach (var output in modified.AsEnumerable().Reverse())
            {
                try { _client.SetInputSettings(mapping[output], snapshots[output], false); }
                catch (Exception rollbackEx) { rollbackErrors.Add($"{output}: {Friendly(rollbackEx)}"); }
            }
            if (sceneChanged && originalScene is not null)
            {
                try { _client.SetCurrentProgramScene(originalScene); }
                catch (Exception rollbackEx) { rollbackErrors.Add($"scene: {Friendly(rollbackEx)}"); }
            }
            var message = $"OBS publish failed: {Friendly(ex)}";
            if (rollbackErrors.Count > 0) message += $" Rollback also failed ({string.Join("; ", rollbackErrors)}).";
            return PublishResult.Failure(message, rollbackErrors.Count > 0);
        }
    }

    private ObsDiscovery DiscoverCore()
    {
        var inputs = _client.GetInputList().Select(ToInputInfo).Where(x => x is not null).Cast<ObsInputInfo>().ToList();
        var scenes = _client.ListScenes().Select(x => x.Name).ToList();
        return new(inputs, scenes);
    }

    internal static string? ValidateMappings(IReadOnlyDictionary<ObsOutput, string> mappings, IReadOnlyList<ObsInputInfo> inputs)
    {
        if (mappings.Values.Any(string.IsNullOrWhiteSpace)) return "All eight OBS source mappings are required.";
        var duplicate = mappings.Values.GroupBy(x => x, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) return $"OBS source '{duplicate.Key}' is assigned more than once.";
        var lookup = inputs.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var pair in mappings)
        {
            if (!lookup.TryGetValue(pair.Value, out var input)) return $"Mapped OBS source '{pair.Value}' does not exist.";
            var valid = pair.Key switch
            {
                ObsOutput.Credits => input.Category == ObsInputCategory.FreeTypeText,
                ObsOutput.Title or ObsOutput.Artist or ObsOutput.DifficultyName or ObsOutput.DifficultyNumber => input.Category is ObsInputCategory.Text or ObsInputCategory.FreeTypeText,
                ObsOutput.Jacket or ObsOutput.DifficultyImage => input.Category == ObsInputCategory.Image,
                ObsOutput.ShowcaseVideo => input.Category == ObsInputCategory.Media,
                _ => false
            };
            if (!valid) return $"OBS source '{input.Name}' has incompatible kind '{input.Kind}' for {pair.Key}.";
        }
        return null;
    }

    internal static IReadOnlyDictionary<ObsOutput, string> Project(ChartInfo chart, string difficultyImagePath) =>
        new Dictionary<ObsOutput, string>
        {
            [ObsOutput.Title] = chart.Title,
            [ObsOutput.Artist] = chart.Artist,
            [ObsOutput.Credits] = chart.CreditsText,
            [ObsOutput.DifficultyName] = chart.DifficultyName,
            [ObsOutput.DifficultyNumber] = ChartFormatter.FormatDifficulty(chart.DifficultyNumber),
            [ObsOutput.Jacket] = Path.GetFullPath(chart.JacketPath),
            [ObsOutput.DifficultyImage] = difficultyImagePath,
            [ObsOutput.ShowcaseVideo] = Path.GetFullPath(chart.ShowcaseVideoPath)
        };

    private static HashSet<ObsOutput> SelectOutputs(PublishMode mode, IReadOnlyDictionary<ObsOutput, string> current, IReadOnlyDictionary<ObsOutput, string>? previous)
    {
        if (mode is PublishMode.Pop or PublishMode.Retry || previous is null) return Enum.GetValues<ObsOutput>().ToHashSet();
        return current.Where(pair => !StringComparer.Ordinal.Equals(pair.Value, previous[pair.Key])).Select(pair => pair.Key).ToHashSet();
    }

    private void SetValue(string inputName, ObsOutput output, string value)
    {
        var key = GetSettingKey(output);
        _client.SetInputSettings(inputName, new JObject { [key] = value }, true);
    }

    internal static string GetSettingKey(ObsOutput output) => output switch
    {
        ObsOutput.Jacket or ObsOutput.DifficultyImage => "file",
        ObsOutput.ShowcaseVideo => "local_file",
        _ => "text"
    };

    private static ObsInputInfo? ToInputInfo(InputBasicInfo input)
    {
        var kind = string.IsNullOrWhiteSpace(input.UnversionedKind) ? input.InputKind : input.UnversionedKind;
        var category = kind switch
        {
            "text_ft2_source" => ObsInputCategory.FreeTypeText,
            "text_gdiplus" => ObsInputCategory.Text,
            "image_source" => ObsInputCategory.Image,
            "ffmpeg_source" => ObsInputCategory.Media,
            _ => (ObsInputCategory?)null
        };
        return category is null ? null : new(input.InputName, input.InputKind, kind, category.Value);
    }

    private void ArmCycle(string inputName, string exitScene)
    {
        lock (_cycleLock)
        {
            _pendingExitScene = null;
            _cycle = new(++_generation, inputName, exitScene, false);
        }
    }

    private void DisarmCycle()
    {
        lock (_cycleLock) _cycle = null;
    }

    private void StartPlaybackPoll()
    {
        PlaybackCycle? cycle;
        lock (_cycleLock) cycle = _cycle;
        if (cycle is null) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                PlaybackCycle? current;
                lock (_cycleLock) current = _cycle;
                if (current?.Generation != cycle.Generation || !_client.IsConnected) return;
                try
                {
                    var status = await WithClientAsync(() => _client.GetMediaInputStatus(cycle.InputName), CancellationToken.None);
                    if (status.State == MediaState.OBS_MEDIA_STATE_PLAYING)
                    {
                        MarkStarted(cycle.InputName);
                        return;
                    }
                }
                catch { return; }
            }
            RaiseStatus("Showcase playback did not enter the playing state; automatic exit is still armed.");
        });
    }

    private void OnPlaybackStarted(object? sender, MediaInputPlaybackStartedEventArgs e) => MarkStarted(e.InputName);

    private void MarkStarted(string? inputName)
    {
        if (string.IsNullOrWhiteSpace(inputName)) return;
        lock (_cycleLock)
        {
            if (_cycle?.InputName == inputName) _cycle = _cycle with { ObservedStarted = true };
        }
    }

    private void OnPlaybackEnded(object? sender, MediaInputPlaybackEndedEventArgs e)
    {
        PlaybackCycle? cycle;
        lock (_cycleLock) cycle = _cycle;
        if (cycle is null || !cycle.ObservedStarted || cycle.InputName != e.InputName) return;
        _ = CompleteCycleAsync(cycle);
    }

    private async Task CompleteCycleAsync(PlaybackCycle cycle)
    {
        lock (_cycleLock)
        {
            if (_cycle?.Generation != cycle.Generation) return;
            _cycle = null;
        }
        try
        {
            await WithClientAsync(() => { _client.SetCurrentProgramScene(cycle.ExitScene); return true; }, CancellationToken.None);
            RaiseStatus($"Showcase completed; switched to '{cycle.ExitScene}'.");
        }
        catch (Exception ex)
        {
            lock (_cycleLock) _pendingExitScene = cycle.ExitScene;
            RaiseStatus($"Exit scene switch failed: {Friendly(ex)} Use Retry Exit Scene.");
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        ConnectionChanged?.Invoke(this, true);
        RaiseStatus("Connected to OBS.");
        _ = RecoverCycleAsync();
    }

    private void OnDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        ConnectionChanged?.Invoke(this, false);
        RaiseStatus($"OBS disconnected: {e.DisconnectReason ?? e.ObsCloseCode.ToString()}.");
        if (!_intentionalDisconnect && !_disposed && !string.IsNullOrWhiteSpace(_url)) _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        for (var attempt = 1; attempt <= 10 && !_intentionalDisconnect && !_disposed && !_client.IsConnected; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 15)));
            try { await ConnectAsync(_url, _password); return; }
            catch (Exception ex) { RaiseStatus($"OBS reconnect attempt {attempt} failed: {Friendly(ex)}"); }
        }
    }

    private async Task RecoverCycleAsync()
    {
        PlaybackCycle? cycle;
        lock (_cycleLock) cycle = _cycle;
        if (cycle is null) return;
        try
        {
            var status = await WithClientAsync(() => _client.GetMediaInputStatus(cycle.InputName), CancellationToken.None);
            if (status.State == MediaState.OBS_MEDIA_STATE_ENDED)
            {
                lock (_cycleLock) _cycle = cycle with { ObservedStarted = true };
                await CompleteCycleAsync(cycle with { ObservedStarted = true });
            }
            else if (status.State == MediaState.OBS_MEDIA_STATE_PLAYING) MarkStarted(cycle.InputName);
        }
        catch (Exception ex) { RaiseStatus($"Could not recover showcase status: {Friendly(ex)}"); }
    }

    private async Task<T> WithClientAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("OBS is not connected.");
        await _requestGate.WaitAsync(cancellationToken);
        try { return await Task.Run(action, cancellationToken); }
        finally { _requestGate.Release(); }
    }

    private void RaiseStatus(string message) => StatusChanged?.Invoke(this, message);
    private static string Friendly(Exception ex) => ex is AggregateException aggregate ? aggregate.GetBaseException().Message : ex.Message;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        _client.Connected -= OnConnected;
        _client.Disconnected -= OnDisconnected;
        _client.MediaInputPlaybackStarted -= OnPlaybackStarted;
        _client.MediaInputPlaybackEnded -= OnPlaybackEnded;
        _requestGate.Dispose();
    }

    private sealed record PlaybackCycle(long Generation, string InputName, string ExitScene, bool ObservedStarted);
}
