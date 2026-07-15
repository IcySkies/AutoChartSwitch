using System.Windows;
using AutoChartSwitch.Core;
using AutoChartSwitch.Obs;

namespace AutoChartSwitch.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private bool _isExiting;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, "SVC-AS.AutoChartSwitch", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Auto Chart Switch is already running.",
                "Auto Chart Switch",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var persistence = new AppPersistence();
        var settings = await persistence.LoadSettingsAsync();
        var charts = await persistence.LoadQueueAsync();
        var queue = new ChartQueue(charts);
        var validator = new ChartValidator();
        var publisher = new ObsChartPublisher();
        var workflow = new ChartWorkflow(queue, validator, publisher);
        _viewModel = new MainViewModel(queue, validator, publisher, workflow, persistence, settings);

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        ConfigureNotificationArea(window);
        window.Show();
        await _viewModel.InitializeAsync();
    }

    private void ConfigureNotificationArea(MainWindow window)
    {
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? new System.Drawing.Icon(System.Drawing.SystemIcons.Application, System.Drawing.SystemIcons.Application.Size);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitApplicationAsync());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _trayIcon,
            Text = "Auto Chart Switch",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreMainWindow();
        window.Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;

        e.Cancel = true;
        MainWindow?.Hide();
    }

    private void RestoreMainWindow()
    {
        if (MainWindow is null) return;

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting) return;

        _isExiting = true;
        try
        {
            if (_viewModel is not null) await _viewModel.ShutdownAsync();
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _trayIcon?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
