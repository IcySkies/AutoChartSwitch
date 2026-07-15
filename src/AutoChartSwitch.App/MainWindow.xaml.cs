using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoChartSwitch.App;

public partial class MainWindow : Window
{
    private QueueRowViewModel? _draggedRow;
    private System.Windows.Point _dragStart;

    public MainWindow() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void QueueGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource);
        _draggedRow = row?.Item as QueueRowViewModel;
        _dragStart = e.GetPosition(QueueGrid);
    }

    private void QueueGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedRow is null) return;
        var current = e.GetPosition(QueueGrid);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        System.Windows.DragDrop.DoDragDrop(QueueGrid, _draggedRow, System.Windows.DragDropEffects.Move);
    }

    private void QueueGrid_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var row = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource);
        if (_draggedRow is not null && row?.Item is QueueRowViewModel target && target != _draggedRow)
            ViewModel?.MoveTo(_draggedRow, target);
        _draggedRow = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ObsPasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is PasswordBox box) box.Password = ViewModel.Settings.ObsPassword;
    }

    private void ObsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is PasswordBox box) ViewModel.Settings.ObsPassword = box.Password;
    }

    private void SettingsChanged(object sender, RoutedEventArgs e) => _ = ViewModel?.SaveSettingsAsync();
    private void SettingsPanel_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _ = ViewModel?.SaveSettingsAsync();
}
