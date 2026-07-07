using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using YiboCodexHUD.Desktop.ViewModels;

namespace YiboCodexHUD.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _persistBoundsTimer;
    private bool _isApplyingStoredBounds;

    public SettingsWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        _persistBoundsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _persistBoundsTimer.Tick += OnPersistBoundsTimerTick;

        Loaded += OnLoaded;
        LocationChanged += OnWindowBoundsChanged;
        SizeChanged += OnWindowBoundsChanged;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

        _isApplyingStoredBounds = true;
        try
        {
            viewModel.ApplySettingsWindowBounds(this);
        }
        finally
        {
            _isApplyingStoredBounds = false;
        }
    }

    private async void OnPersistBoundsTimerTick(object? sender, EventArgs e)
    {
        _persistBoundsTimer.Stop();

        if (_isApplyingStoredBounds || !IsLoaded || !IsVisible || WindowState != WindowState.Normal || DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

        await viewModel.PersistSettingsWindowBoundsAsync(this);
    }

    private void OnWindowBoundsChanged(object? sender, EventArgs e)
    {
        if (_isApplyingStoredBounds || !IsLoaded || !IsVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        _persistBoundsTimer.Stop();
        _persistBoundsTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _persistBoundsTimer.Stop();
        _persistBoundsTimer.Tick -= OnPersistBoundsTimerTick;
        Loaded -= OnLoaded;
        LocationChanged -= OnWindowBoundsChanged;
        SizeChanged -= OnWindowBoundsChanged;
        Closed -= OnClosed;
    }
}
