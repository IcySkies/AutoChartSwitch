using System.Collections.ObjectModel;
using System.Windows;
using AutoChartSwitch.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AutoChartSwitch.App;

public partial class MainViewModel : ObservableObject
{
    private readonly IChartQueue _queue;
    private readonly IChartValidator _validator;
    private readonly IChartPublisher _publisher;
    private readonly ChartWorkflow _workflow;
    private readonly AppPersistence _persistence;
    private readonly ChartInterchange _interchange;

    public AutoChartSettings Settings { get; }
    public ObservableCollection<QueueRowViewModel> QueueRows { get; } = [];
    public ObservableCollection<string> TextInputs { get; } = [];
    public ObservableCollection<string> FreeTypeInputs { get; } = [];
    public ObservableCollection<string> ImageInputs { get; } = [];
    public ObservableCollection<string> MediaInputs { get; } = [];
    public ObservableCollection<string> Scenes { get; } = [];

    [ObservableProperty] private QueueRowViewModel? selectedRow;
    [ObservableProperty] private ChartInfo? currentDisplay;
    [ObservableProperty] private string statusText = "Ready. Current display is blank.";
    [ObservableProperty] private string connectionText = "Disconnected";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasExitError;

    public string CurrentDifficulty => CurrentDisplay is null ? "" : $"{CurrentDisplay.DifficultyName} {ChartFormatter.FormatDifficulty(CurrentDisplay.DifficultyNumber)}".TrimEnd();
    public string CurrentCredits => CurrentDisplay?.CreditsText ?? "";
    public string CurrentTechStats => CurrentDisplay is null ? "" : FormatTechStats(CurrentDisplay.TechStats);
    public bool HasCurrent => CurrentDisplay is not null;
    public event EventHandler? ShowTechStatsRequested;

    public MainViewModel(IChartQueue queue, IChartValidator validator, IChartPublisher publisher,
        ChartWorkflow workflow, AppPersistence persistence, AutoChartSettings settings)
    {
        _queue = queue;
        _validator = validator;
        _publisher = publisher;
        _workflow = workflow;
        _persistence = persistence;
        _interchange = new(validator);
        Settings = settings;
        _queue.Changed += OnQueueChanged;
        _workflow.CurrentDisplayChanged += OnCurrentDisplayChanged;
        _publisher.StatusChanged += OnPublisherStatusChanged;
        _publisher.ConnectionChanged += OnConnectionChanged;
        RefreshRows();
    }

    public async Task InitializeAsync()
    {
        if (Settings.AutoConnect) await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (_publisher.IsConnected)
            {
                await _publisher.DisconnectAsync();
                return;
            }
            await SaveSettingsAsync();
            StatusText = "Connecting to OBS...";
            await _publisher.ConnectAsync(Settings.ObsUrl.Trim(), Settings.ObsPassword);
            await RefreshSourcesAsync();
        }
        catch (Exception ex) { ShowError("Could not connect to OBS", ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        if (!_publisher.IsConnected) { StatusText = "Connect to OBS before refreshing sources."; return; }
        try
        {
            var discovery = await _publisher.DiscoverAsync();
            Replace(TextInputs, discovery.Inputs.Where(x => x.Category is ObsInputCategory.Text or ObsInputCategory.FreeTypeText).Select(x => x.Name));
            Replace(FreeTypeInputs, discovery.Inputs.Where(x => x.Category == ObsInputCategory.FreeTypeText).Select(x => x.Name));
            Replace(ImageInputs, discovery.Inputs.Where(x => x.Category == ObsInputCategory.Image).Select(x => x.Name));
            Replace(MediaInputs, discovery.Inputs.Where(x => x.Category == ObsInputCategory.Media).Select(x => x.Name));
            Replace(Scenes, discovery.Scenes);
            StatusText = $"Loaded {discovery.Inputs.Count} compatible inputs and {discovery.Scenes.Count} scenes.";
        }
        catch (Exception ex) { ShowError("Source refresh failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanMutateQueue))]
    private void InsertFront() => EditAndInsert(true);

    [RelayCommand(CanExecute = nameof(CanMutateQueue))]
    private void InsertBack() => EditAndInsert(false);

    private void EditAndInsert(bool front)
    {
        var chart = ChartEditorWindow.Edit(null, front ? "Insert Chart at Front" : "Insert Chart at Back", Settings.DifficultyCustomPath, _validator);
        if (chart is null) return;
        if (front) _queue.InsertFront(chart); else _queue.InsertBack(chart);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditSelected()
    {
        if (SelectedRow is null) return;
        var edited = ChartEditorWindow.Edit(SelectedRow.Chart, "Edit Queued Chart", Settings.DifficultyCustomPath, _validator);
        if (edited is not null) _queue.Replace(SelectedRow.Chart.Id, edited);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (SelectedRow is null) return;
        if (MessageBox.Show($"Delete '{SelectedRow.Title}' from the queue?", "Delete Chart", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _queue.Delete(SelectedRow.Chart.Id);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedRow is null) return;
        var index = QueueRows.IndexOf(SelectedRow);
        _queue.Move(SelectedRow.Chart.Id, index - 1);
        SelectedRow = QueueRows[Math.Max(0, index - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedRow is null) return;
        var index = QueueRows.IndexOf(SelectedRow);
        _queue.Move(SelectedRow.Chart.Id, index + 1);
        SelectedRow = QueueRows[Math.Min(QueueRows.Count - 1, index + 1)];
    }

    public void MoveTo(QueueRowViewModel source, QueueRowViewModel target)
    {
        if (IsBusy) return;
        _queue.Move(source.Chart.Id, QueueRows.IndexOf(target));
        SelectedRow = QueueRows.FirstOrDefault(x => x.Chart.Id == source.Chart.Id);
    }

    [RelayCommand(CanExecute = nameof(CanPop))]
    private async Task PopAsync()
    {
        await RunPublishAsync(() => _workflow.PopFrontAsync(Settings));
    }

    [RelayCommand(CanExecute = nameof(HasCurrentDisplay))]
    private async Task QuickEditAsync()
    {
        if (CurrentDisplay is null) return;
        var edited = ChartEditorWindow.Edit(CurrentDisplay, "Quick Edit Current Display", Settings.DifficultyCustomPath, _validator);
        if (edited is null) return;
        await RunPublishAsync(() => _workflow.QuickEditAsync(edited, Settings));
    }

    [RelayCommand(CanExecute = nameof(HasCurrentDisplay))]
    private async Task RetrySyncAsync() => await RunPublishAsync(() => _workflow.RetrySyncAsync(Settings));

    [RelayCommand]
    private void ShowTechStats() => ShowTechStatsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RetryExitAsync()
    {
        var result = await _publisher.RetryExitSceneAsync();
        StatusText = result.Message;
        HasExitError = !result.Succeeded;
    }

    [RelayCommand(CanExecute = nameof(CanMutateQueue))]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Chart list JSON (*.json)|*.json", Title = "Import Chart List" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var preview = await _interchange.PreviewImportAsync(dialog.FileName);
            var warning = preview.Warnings.Count == 0 ? "" : $"\n\nWarnings:\n{string.Join("\n", preview.Warnings.Take(8))}";
            var choice = MessageBox.Show($"Import {preview.Charts.Count} charts?\n\nYes: append\nNo: replace queue{warning}",
                "Import Chart List", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes) _queue.Append(preview.Charts);
            else if (choice == MessageBoxResult.No) _queue.ReplaceAll(preview.Charts);
        }
        catch (Exception ex) { ShowError("Import failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Chart list JSON (*.json)|*.json", FileName = "charts.json", Title = "Export Chart List" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _interchange.ExportAsync(dialog.FileName, _queue.Items);
            StatusText = $"Exported {_queue.Items.Count} charts.";
        }
        catch (Exception ex) { ShowError("Export failed", ex.Message); }
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        try { await _persistence.SaveSettingsAsync(Settings); }
        catch (Exception ex) { StatusText = $"Settings autosave failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void BrowseDifficultyFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select the difficulty image folder", Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            Settings.DifficultyCustomPath = dialog.FolderName;
            OnPropertyChanged(nameof(Settings));
            _ = SaveSettingsAsync();
            RefreshRows();
        }
    }

    private async Task RunPublishAsync(Func<Task<PublishResult>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        await SaveSettingsAsync();
        try
        {
            var result = await action();
            StatusText = result.Message;
            if (!result.Succeeded) MessageBox.Show(result.Message, "OBS operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) { ShowError("OBS operation failed", ex.Message); }
        finally { IsBusy = false; }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        RefreshRows();
        PopCommand.NotifyCanExecuteChanged();
        _ = AutosaveQueueAsync();
    }

    private async Task AutosaveQueueAsync()
    {
        try { await _persistence.SaveQueueAsync(_queue.Items); }
        catch (Exception ex) { StatusText = $"Queue autosave failed: {ex.Message}"; }
    }

    private void RefreshRows()
    {
        var selectedId = SelectedRow?.Chart.Id;
        QueueRows.Clear();
        foreach (var chart in _queue.Items)
        {
            var validation = _validator.Validate(chart, Settings.DifficultyCustomPath, true);
            QueueRows.Add(new(chart, ChartFormatter.FormatDifficulty(chart.DifficultyNumber), validation.Summary));
        }
        SelectedRow = selectedId is null ? null : QueueRows.FirstOrDefault(x => x.Chart.Id == selectedId);
    }

    private void OnCurrentDisplayChanged(object? sender, EventArgs e)
    {
        CurrentDisplay = _workflow.CurrentDisplay;
        OnPropertyChanged(nameof(CurrentDifficulty));
        OnPropertyChanged(nameof(CurrentCredits));
        OnPropertyChanged(nameof(CurrentTechStats));
        OnPropertyChanged(nameof(HasCurrent));
        QuickEditCommand.NotifyCanExecuteChanged();
        RetrySyncCommand.NotifyCanExecuteChanged();
    }

    private void OnPublisherStatusChanged(object? sender, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = message;
            if (message.Contains("Retry Exit Scene", StringComparison.Ordinal)) HasExitError = true;
            else if (message.Contains("switched to", StringComparison.OrdinalIgnoreCase)) HasExitError = false;
        });
    }

    private void OnConnectionChanged(object? sender, bool connected) =>
        Application.Current.Dispatcher.Invoke(() => ConnectionText = connected ? "Connected" : "Disconnected");

    partial void OnSelectedRowChanged(QueueRowViewModel? value)
    {
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        InsertFrontCommand.NotifyCanExecuteChanged();
        InsertBackCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        PopCommand.NotifyCanExecuteChanged();
        QuickEditCommand.NotifyCanExecuteChanged();
        RetrySyncCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanMutateQueue() => !IsBusy;
    private bool CanPop() => !IsBusy && QueueRows.Count > 0;
    private bool HasSelection() => !IsBusy && SelectedRow is not null;
    private bool CanMoveUp() => !IsBusy && SelectedRow is not null && QueueRows.IndexOf(SelectedRow) > 0;
    private bool CanMoveDown() => !IsBusy && SelectedRow is not null && QueueRows.IndexOf(SelectedRow) < QueueRows.Count - 1;
    private bool HasCurrentDisplay() => !IsBusy && CurrentDisplay is not null;

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values.Distinct(StringComparer.Ordinal)) target.Add(value);
    }

    private static string FormatTechStats(ChartTechStats? stats)
    {
        if (stats is null) return "";
        return $"CHIP {stats.Chip:g}   TECH {stats.Tech:g}   STREAM {stats.Stream:g}\n" +
               $"CHORD {stats.Chord:g}   BURST {stats.Burst:g}" +
               (stats.Gimmick > 0 ? $"   GIMMICK {stats.Gimmick:g}" : "");
    }

    private void ShowError(string title, string message)
    {
        StatusText = message;
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public async Task ShutdownAsync()
    {
        await _persistence.SaveQueueAsync(_queue.Items);
        await _persistence.SaveSettingsAsync(Settings);
        await _publisher.DisposeAsync();
    }
}
