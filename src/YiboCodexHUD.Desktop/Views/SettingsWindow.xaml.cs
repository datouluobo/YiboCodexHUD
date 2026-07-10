using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YiboCodexHUD.Desktop.ViewModels;

namespace YiboCodexHUD.Desktop.Views;

public partial class SettingsWindow : Window
{
    private const string GitHubUrl = "https://github.com/datouluobo/YiboCodexHUD";
    private readonly DispatcherTimer _persistBoundsTimer;
    private bool _isApplyingStoredBounds;
    public string AppVersionText { get; } = $"当前版本 {ResolveAppVersion()}";

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

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法打开 GitHub 页面：{ex.Message}",
                "打开链接失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnNudgePositionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

        var (deltaX, deltaY) = GetNudgeDelta(button.Tag as string, stepPx: 1);
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        await viewModel.NudgeCurrentPositionAsync(deltaX, deltaY);
    }

    private async void OnNudgePositionButtonRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

        var (deltaX, deltaY) = GetNudgeDelta(button.Tag as string, stepPx: 5);
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        e.Handled = true;
        await viewModel.NudgeCurrentPositionAsync(deltaX, deltaY);
    }

    private static (int DeltaX, int DeltaY) GetNudgeDelta(string? direction, int stepPx) => direction switch
    {
        "Left" => (-stepPx, 0),
        "Right" => (stepPx, 0),
        "Up" => (0, -stepPx),
        "Down" => (0, stepPx),
        _ => (0, 0)
    };

    private static string ResolveAppVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "未知版本";
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
