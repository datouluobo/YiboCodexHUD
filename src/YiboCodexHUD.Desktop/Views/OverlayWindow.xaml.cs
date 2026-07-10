using System.Windows.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Documents;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;
using YiboCodexHUD.Desktop.ViewModels;

namespace YiboCodexHUD.Desktop.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const int MenuDismissTicksBeforeClose = 5;
    private const double InteractionDotKeepOpenPaddingPx = 18d;
    private const double ContextMenuKeepOpenPaddingPx = 14d;

    private readonly ICodexWindowTracker _windowTracker;
    private readonly SettingsWindow _settingsWindow;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _hoverTimer;
    private bool _interactionMenuOpen;
    private bool _isWindowClickThroughEnabled;
    private int _menuDismissTickCount;

    public OverlayWindow(OverlayViewModel viewModel, ICodexWindowTracker windowTracker, SettingsWindow settingsWindow)
    {
        InitializeComponent();
        DataContext = viewModel;
        _windowTracker = windowTracker;
        _settingsWindow = settingsWindow;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += OnPositionTimerTick;

        _hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _hoverTimer.Tick += OnHoverTimerTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 40;
        Top = 40;

        UpdatePosition();
        _positionTimer.Start();
        _hoverTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        SourceInitialized -= OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SetClickThroughEnabled(true);
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        UpdatePosition();
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

        var isHudHovered = IsCursorWithinElementBounds(this);
        viewModel.IsHudHovered = isHudHovered || _interactionMenuOpen;
        viewModel.IsInteractionDotActive = IsCursorWithinElementBounds(InteractionDotHost, inflateByPixels: 10d) || _interactionMenuOpen;

        if (_interactionMenuOpen)
        {
            var shouldKeepMenuOpen = IsCursorWithinElementBounds(InteractionDotHost, inflateByPixels: InteractionDotKeepOpenPaddingPx)
                || IsCursorWithinContextMenuBounds(InteractionMenu, inflateByPixels: ContextMenuKeepOpenPaddingPx)
                || IsAnySubmenuOpen(InteractionMenu);

            _menuDismissTickCount = shouldKeepMenuOpen ? 0 : _menuDismissTickCount + 1;
            if (_menuDismissTickCount >= MenuDismissTicksBeforeClose && InteractionMenu.IsOpen)
            {
                InteractionMenu.IsOpen = false;
            }

            SetClickThroughEnabled(false);
            return;
        }

        _menuDismissTickCount = 0;
        SetClickThroughEnabled(!IsCursorWithinElementBounds(InteractionDotHost, inflateByPixels: 10d));
    }

    private void OnInteractionDotMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (InteractionDotHost.ContextMenu is not { } contextMenu)
        {
            return;
        }

        contextMenu.PlacementTarget = InteractionDotHost;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void OnInteractionMenuOpened(object sender, RoutedEventArgs e)
    {
        _interactionMenuOpen = true;
        _menuDismissTickCount = 0;
        SetClickThroughEnabled(false);
    }

    private void OnInteractionMenuClosed(object sender, RoutedEventArgs e)
    {
        _interactionMenuOpen = false;
        _menuDismissTickCount = 0;
    }

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void OnOpenSettingsMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow.Owner is null)
        {
            _settingsWindow.Owner = this;
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    private void UpdatePosition()
    {
        var trackedWindow = _windowTracker.GetTrackedWindow();
        if (trackedWindow is null || !trackedWindow.IsTopVisible)
        {
            HideOverlay();
            return;
        }

        ShowOverlay();

        var viewModel = DataContext as OverlayViewModel;
        var anchorBounds = trackedWindow.IsMaximized
            ? trackedWindow.MonitorBounds
            : trackedWindow.Bounds;
        var safeBounds = trackedWindow.TitleBarSafeBounds;
        var contentWidthPx = Math.Max(0d, safeBounds.Width);

        if (DisplayTextBlock is TextBlock textBlock)
        {
            ApplyDisplaySegments(textBlock, SelectDisplaySegments(viewModel, textBlock, trackedWindow, contentWidthPx));
            textBlock.MaxWidth = PixelsToDipX(Math.Max(120d, contentWidthPx), trackedWindow.Handle);
        }

        UpdateLayout();

        var titleBarHeightPx = Math.Max(38d, trackedWindow.ClientBounds.Top - anchorBounds.Top);
        var overlayHeightPx = DipToPixelsY(ActualHeight, trackedWindow.Handle);
        var titleTopPx = anchorBounds.Top + Math.Max(6d, ((titleBarHeightPx - overlayHeightPx) / 2.0));
        var safeLeftPx = safeBounds.Left;
        var safeRightPx = safeBounds.Left + safeBounds.Width;
        var conservativeLeftDockPx = GetConservativeLeftDockPx(anchorBounds, trackedWindow.ClientBounds);
        var safeCenterXPx = safeLeftPx + ((safeRightPx - safeLeftPx) / 2.0);
        var overlayWidthPx = DipToPixelsX(ActualWidth, trackedWindow.Handle);
        var minLeftPx = Math.Max(safeLeftPx, conservativeLeftDockPx);
        var maxLeftPx = Math.Max(safeLeftPx, safeRightPx - overlayWidthPx);
        var offsetXPx = viewModel?.PositionOffsetX ?? 0d;
        var offsetYPx = viewModel?.PositionOffsetY ?? 0d;
        var desiredLeftPx = viewModel switch
        {
            { IsHorizontalAlignmentLeft: true } => minLeftPx,
            { IsHorizontalAlignmentRight: true } => safeRightPx - overlayWidthPx,
            _ => safeCenterXPx - (overlayWidthPx / 2.0)
        };

        Left = PixelsToDipX(Math.Max(minLeftPx, Math.Min(desiredLeftPx + offsetXPx, maxLeftPx)), trackedWindow.Handle);
        Top = PixelsToDipY(titleTopPx + offsetYPx, trackedWindow.Handle);
    }

    private static double GetConservativeLeftDockPx(WindowBounds anchorBounds, WindowBounds clientBounds)
    {
        var width = Math.Max(1, anchorBounds.Width);
        var fallbackInset = width switch
        {
            < 900 => 340d,
            < 1100 => 370d,
            < 1400 => 410d,
            _ => 440d
        };

        // Keep the left-docked HUD behind the app's menu/help cluster instead of hugging the raw caption edge.
        return Math.Max(anchorBounds.Left + fallbackInset, clientBounds.Left + 12d);
    }

    private IReadOnlyList<OverlayViewModel.StyledDisplaySegment> SelectDisplaySegments(
        OverlayViewModel? viewModel,
        TextBlock textBlock,
        TrackedWindow trackedWindow,
        double contentWidthPx)
    {
        if (viewModel is null)
        {
            return Array.Empty<OverlayViewModel.StyledDisplaySegment>();
        }

        if (viewModel.IsDisplayModeAuto)
        {
            foreach (var candidate in viewModel.GetAutoDisplaySegmentCandidates())
            {
                if (MeasureTextWidthPx(JoinSegmentText(candidate), textBlock, trackedWindow.Handle) <= contentWidthPx)
                {
                    return candidate;
                }
            }

            return Array.Empty<OverlayViewModel.StyledDisplaySegment>();
        }

        return viewModel.SelectDisplaySegments(contentWidthPx);
    }

    private static string JoinSegmentText(IReadOnlyList<OverlayViewModel.StyledDisplaySegment> segments) =>
        string.Concat(segments.Select(static segment => segment.Text));

    private static void ApplyDisplaySegments(TextBlock textBlock, IReadOnlyList<OverlayViewModel.StyledDisplaySegment> segments)
    {
        textBlock.Inlines.Clear();
        foreach (var segment in segments)
        {
            textBlock.Inlines.Add(new Run(segment.Text)
            {
                Foreground = segment.Foreground
            });
        }
    }

    private void HideOverlay()
    {
        CloseInteractionMenu();

        if (Visibility != Visibility.Hidden)
        {
            Visibility = Visibility.Hidden;
        }
    }

    private void ShowOverlay()
    {
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }
    }

    private void CloseInteractionMenu()
    {
        if (InteractionMenu.IsOpen)
        {
            InteractionMenu.IsOpen = false;
        }

        _interactionMenuOpen = false;
        _menuDismissTickCount = 0;
    }

    private void SetClickThroughEnabled(bool enabled)
    {
        if (!IsLoaded || _isWindowClickThroughEnabled == enabled)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        var desiredStyle = enabled
            ? extendedStyle | WsExTransparent | WsExToolWindow | WsExNoActivate
            : (extendedStyle & ~WsExTransparent) | WsExToolWindow | WsExNoActivate;

        SetWindowLong(handle, GwlExStyle, desiredStyle);
        _isWindowClickThroughEnabled = enabled;
    }

    private bool IsCursorWithinElementBounds(FrameworkElement element, double inflateByPixels = 0d)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0d || element.ActualHeight <= 0d || !GetCursorPos(out var cursorPoint))
        {
            return false;
        }

        PresentationSource? source = PresentationSource.FromVisual(element);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        var topLeft = element.PointToScreen(new Point(0d, 0d));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

        return cursorPoint.X >= topLeft.X - inflateByPixels
            && cursorPoint.X <= bottomRight.X + inflateByPixels
            && cursorPoint.Y >= topLeft.Y - inflateByPixels
            && cursorPoint.Y <= bottomRight.Y + inflateByPixels;
    }

    private bool IsCursorWithinContextMenuBounds(ContextMenu menu, double inflateByPixels = 0d)
    {
        if (!menu.IsOpen || menu.ActualWidth <= 0d || menu.ActualHeight <= 0d || !GetCursorPos(out var cursorPoint))
        {
            return false;
        }

        try
        {
            var topLeft = menu.PointToScreen(new Point(0d, 0d));
            var bottomRight = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));

            return cursorPoint.X >= topLeft.X - inflateByPixels
                && cursorPoint.X <= bottomRight.X + inflateByPixels
                && cursorPoint.Y >= topLeft.Y - inflateByPixels
                && cursorPoint.Y <= bottomRight.Y + inflateByPixels;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAnySubmenuOpen(ItemsControl itemsControl)
    {
        foreach (var item in itemsControl.Items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            if (menuItem.IsSubmenuOpen || IsAnySubmenuOpen(menuItem))
            {
                return true;
            }
        }

        return false;
    }

    private double MeasureTextWidthPx(string text, TextBlock textBlock, nint targetWindowHandle)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0d;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize,
            Brushes.Transparent,
            dpi.PixelsPerDip);

        return DipToPixelsX(formattedText.WidthIncludingTrailingWhitespace, targetWindowHandle);
    }

    private double PixelsToDipX(double pixelValue, nint targetWindowHandle)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return pixelValue;
        }

        var matrix = source.CompositionTarget.TransformFromDevice;
        return matrix.Transform(new Point(pixelValue, 0d)).X;
    }

    private double PixelsToDipY(double pixelValue, nint targetWindowHandle)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return pixelValue;
        }

        var matrix = source.CompositionTarget.TransformFromDevice;
        return matrix.Transform(new Point(0d, pixelValue)).Y;
    }

    private double DipToPixelsX(double dipValue, nint targetWindowHandle)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return dipValue;
        }

        var matrix = source.CompositionTarget.TransformToDevice;
        return matrix.Transform(new Point(dipValue, 0d)).X;
    }

    private double DipToPixelsY(double dipValue, nint targetWindowHandle)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return dipValue;
        }

        var matrix = source.CompositionTarget.TransformToDevice;
        return matrix.Transform(new Point(0d, dipValue)).Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
