using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;
using YiboCodexHUD.Core.Utilities;

namespace YiboCodexHUD.WindowsInterop.Services;

public sealed partial class Win32CodexWindowTracker : ICodexWindowTracker
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int HtCaption = 2;
    private const int SwRestore = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRootOwner = 3;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int MinimumTrackedWindowWidth = 320;
    private const int MinimumTrackedWindowHeight = 180;
    private const nuint WsCaption = 0x00C00000;
    private const nuint WsSysMenu = 0x00080000;
    private const nuint WsMinimizeBox = 0x00020000;
    private const nuint WsMaximizeBox = 0x00010000;
    private const nuint WsExAppWindow = 0x00040000;
    private const nuint WsExToolWindow = 0x00000080;
    private const nuint WsExNoActivate = 0x08000000;
    private const nuint WsExTransparent = 0x00000020;
    private const nuint WsExLayered = 0x00080000;
    private static readonly int CurrentProcessId = Environment.ProcessId;

    public TrackedWindow? GetTrackedWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        TrackedWindow? bestMatch = null;
        var bestScore = int.MinValue;

        EnumWindows((windowHandle, _) =>
        {
            var candidate = TryCreateTrackedWindow(windowHandle, foregroundWindow, out var candidateScore);
            if (candidate is null)
            {
                return true;
            }

            if (bestMatch is null || candidateScore > bestScore)
            {
                bestMatch = candidate;
                bestScore = candidateScore;
            }

            return true;
        }, IntPtr.Zero);

        return bestMatch;
    }

    public bool TryActivateTrackedWindow()
    {
        var trackedWindow = GetTrackedWindow();
        if (trackedWindow is null)
        {
            return false;
        }

        var windowHandle = trackedWindow.Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            _ = ShowWindow(windowHandle, SwRestore);
        }

        _ = BringWindowToTop(windowHandle);
        return SetForegroundWindow(windowHandle);
    }

    private static TrackedWindow? TryCreateTrackedWindow(IntPtr windowHandle, IntPtr foregroundWindow, out int candidateScore)
    {
        candidateScore = int.MinValue;

        if (windowHandle == IntPtr.Zero || !IsWindowVisible(windowHandle))
        {
            return null;
        }

        if (IsWindowCloaked(windowHandle) || HasWindowOwner(windowHandle) || !IsRootOwner(windowHandle))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);

        string processName;
        try
        {
            if (processId == CurrentProcessId)
            {
                return null;
            }

            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return null;
        }

        var titleLength = GetWindowTextLength(windowHandle);
        var titleBuilder = new StringBuilder(Math.Max(titleLength, 0) + 1);
        _ = GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString().Trim();
        var className = GetWindowClassName(windowHandle);
        var style = GetWindowStyle(windowHandle, GwlStyle);
        var exStyle = GetWindowStyle(windowHandle, GwlExStyle);

        if (processName.Contains("YiboCodexHUD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var processLooksLikeCodex = CodexDesktopIdentity.MatchesProcessName(processName);

        if (!processLooksLikeCodex)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title) && windowHandle != foregroundWindow)
        {
            return null;
        }

        if (!TryGetWindowBounds(windowHandle, out var bounds))
        {
            return null;
        }

        if (!TryGetClientBounds(windowHandle, out var clientBounds))
        {
            return null;
        }

        if (!TryGetMonitorBounds(windowHandle, out var monitorBounds))
        {
            return null;
        }

        var titleBarSafeBounds = TryGetTitleBarSafeBounds(windowHandle, bounds, clientBounds, out var measuredTitleBarSafeBounds)
            ? measuredTitleBarSafeBounds
            : BuildFallbackTitleBarSafeBounds(bounds, clientBounds);

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < MinimumTrackedWindowWidth || height < MinimumTrackedWindowHeight)
        {
            return null;
        }

        var hasMeasuredTitleBarSafeBounds = titleBarSafeBounds.Width > 0 && titleBarSafeBounds.Height > 0;
        var looksLikePrimaryWindow = LooksLikePrimaryWindow(
            title,
            className,
            width,
            height,
            clientBounds,
            hasMeasuredTitleBarSafeBounds,
            style,
            exStyle);
        if (!looksLikePrimaryWindow)
        {
            return null;
        }

        var isForeground = windowHandle == foregroundWindow;
        var isTopVisible = IsWindowTopVisible(windowHandle, bounds, clientBounds);
        candidateScore = CalculateCandidateScore(
            title,
            className,
            isForeground,
            string.IsNullOrWhiteSpace(title),
            hasMeasuredTitleBarSafeBounds,
            width,
            height,
            clientBounds,
            style,
            exStyle);

        return new TrackedWindow(
            windowHandle,
            string.IsNullOrWhiteSpace(title) ? processName : title,
            new WindowBounds(bounds.Left, bounds.Top, width, height),
            clientBounds,
            monitorBounds,
            titleBarSafeBounds,
            IsForeground: isForeground,
            IsTopVisible: isTopVisible,
            IsMaximized: IsZoomed(windowHandle));
    }

    private static bool IsWindowTopVisible(IntPtr targetWindowHandle, RECT windowBounds, WindowBounds clientBounds)
    {
        foreach (var samplePoint in EnumerateVisibilitySamplePoints(windowBounds, clientBounds))
        {
            var topWindowHandle = WindowFromPoint(samplePoint);
            if (topWindowHandle == IntPtr.Zero)
            {
                continue;
            }

            if (BelongsToCurrentProcess(topWindowHandle))
            {
                continue;
            }

            var rootHandle = GetAncestor(topWindowHandle, GaRoot);
            var rootOwnerHandle = GetAncestor(topWindowHandle, GaRootOwner);
            if (rootHandle == targetWindowHandle || rootOwnerHandle == targetWindowHandle)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<POINT> EnumerateVisibilitySamplePoints(RECT windowBounds, WindowBounds clientBounds)
    {
        var width = Math.Max(1, windowBounds.Right - windowBounds.Left);
        var titleBarHeight = Math.Max(24, clientBounds.Top - windowBounds.Top);
        var y = windowBounds.Top + Math.Max(8, Math.Min(titleBarHeight / 2, titleBarHeight - 6));

        yield return new POINT { X = windowBounds.Left + (width / 2), Y = y };
        yield return new POINT { X = windowBounds.Left + Math.Max(40, width / 3), Y = y };
        yield return new POINT { X = windowBounds.Right - Math.Max(40, width / 3), Y = y };
    }

    private static bool BelongsToCurrentProcess(IntPtr windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == CurrentProcessId;
    }

    private static bool TryGetTitleBarSafeBounds(
        IntPtr windowHandle,
        RECT windowBounds,
        WindowBounds clientBounds,
        out WindowBounds safeBounds)
    {
        safeBounds = default!;

        var width = Math.Max(1, windowBounds.Right - windowBounds.Left);
        var titleBarHeight = Math.Max(24, clientBounds.Top - windowBounds.Top);
        if (width < 200 || titleBarHeight < 24)
        {
            return false;
        }

        var sampleY = windowBounds.Top + Math.Max(8, Math.Min(titleBarHeight / 2, titleBarHeight - 6));
        var step = 8;
        int? leftCaptionX = null;
        int? rightCaptionX = null;

        for (var sampleX = windowBounds.Left + 24; sampleX <= windowBounds.Right - 24; sampleX += step)
        {
            var hitTestResult = HitTestNonClient(windowHandle, sampleX, sampleY);
            if (hitTestResult != HtCaption)
            {
                continue;
            }

            leftCaptionX ??= sampleX;
            rightCaptionX = sampleX;
        }

        if (!leftCaptionX.HasValue || !rightCaptionX.HasValue)
        {
            return false;
        }

        var safeLeft = leftCaptionX.Value + 6;
        var safeRight = rightCaptionX.Value - 6;
        var safeWidth = safeRight - safeLeft;
        if (safeWidth < 120)
        {
            return false;
        }

        safeBounds = new WindowBounds(safeLeft, windowBounds.Top, safeWidth, titleBarHeight);
        return true;
    }

    private static WindowBounds BuildFallbackTitleBarSafeBounds(RECT windowBounds, WindowBounds clientBounds)
    {
        var width = Math.Max(1, windowBounds.Right - windowBounds.Left);
        var titleBarHeight = Math.Max(24, clientBounds.Top - windowBounds.Top);

        var leftInset = width switch
        {
            < 900 => 300,
            < 1100 => 330,
            < 1400 => 360,
            _ => 390
        };

        var rightInset = width switch
        {
            < 900 => 150,
            < 1100 => 160,
            < 1400 => 170,
            _ => 180
        };

        return new WindowBounds(
            windowBounds.Left + leftInset,
            windowBounds.Top,
            Math.Max(120, width - leftInset - rightInset),
            titleBarHeight);
    }

    private static int HitTestNonClient(IntPtr windowHandle, int screenX, int screenY)
    {
        var lParam = (nint)(((screenY & 0xFFFF) << 16) | (screenX & 0xFFFF));
        return unchecked((short)SendMessage(windowHandle, WmNcHitTest, IntPtr.Zero, lParam));
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var classNameBuilder = new StringBuilder(256);
        _ = GetClassName(windowHandle, classNameBuilder, classNameBuilder.Capacity);
        return classNameBuilder.ToString().Trim();
    }

    private static nuint GetWindowStyle(IntPtr windowHandle, int index)
    {
        if (IntPtr.Size == 8)
        {
            return unchecked((nuint)GetWindowLongPtr(windowHandle, index).ToInt64());
        }

        return unchecked((nuint)(uint)GetWindowLong(windowHandle, index));
    }

    private static bool TryGetWindowBounds(IntPtr windowHandle, out RECT rect)
    {
        if (DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(windowHandle, out rect);
    }

    private static bool TryGetClientBounds(IntPtr windowHandle, out WindowBounds clientBounds)
    {
        clientBounds = default!;

        if (!GetClientRect(windowHandle, out var clientRect))
        {
            return false;
        }

        var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };

        if (!ClientToScreen(windowHandle, ref topLeft) || !ClientToScreen(windowHandle, ref bottomRight))
        {
            return false;
        }

        clientBounds = new WindowBounds(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        return clientBounds.Width > 0 && clientBounds.Height > 0;
    }

    private static bool TryGetMonitorBounds(IntPtr windowHandle, out WindowBounds monitorBounds)
    {
        monitorBounds = default!;

        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        monitorBounds = new WindowBounds(
            monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Top,
            monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);

        return monitorBounds.Width > 0 && monitorBounds.Height > 0;
    }

    private static bool IsWindowCloaked(IntPtr windowHandle)
    {
        return DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out int cloaked, Marshal.SizeOf<int>()) == 0
            && cloaked != 0;
    }

    private static bool HasWindowOwner(IntPtr windowHandle) => GetWindow(windowHandle, GwOwner) != IntPtr.Zero;

    private static bool IsRootOwner(IntPtr windowHandle) => GetAncestor(windowHandle, GaRootOwner) == windowHandle;

    private static bool LooksLikePrimaryWindow(
        string title,
        string className,
        int width,
        int height,
        WindowBounds clientBounds,
        bool hasMeasuredTitleBarSafeBounds,
        nuint style,
        nuint exStyle)
    {
        var hasCaption = (style & WsCaption) == WsCaption;
        var hasSystemMenu = (style & WsSysMenu) != 0;
        var hasMinimizeOrMaximizeButton = (style & (WsMinimizeBox | WsMaximizeBox)) != 0;
        var isAppWindow = (exStyle & WsExAppWindow) != 0;
        var isToolWindow = (exStyle & WsExToolWindow) != 0;
        var isNoActivate = (exStyle & WsExNoActivate) != 0;
        var isTransparent = (exStyle & WsExTransparent) != 0;
        var isLayered = (exStyle & WsExLayered) != 0;
        var classLooksLikeDesktopShell =
            className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
            || className.Contains("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase)
            || className.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
        var titleLooksLikeCodex =
            title.Contains("Codex", StringComparison.OrdinalIgnoreCase)
            || title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || title.Contains("OpenAI", StringComparison.OrdinalIgnoreCase);

        var clientArea = Math.Max(1, clientBounds.Width * clientBounds.Height);
        var windowArea = Math.Max(1, width * height);
        var clientFillRatio = clientArea / (double)windowArea;

        if (isToolWindow || isNoActivate)
        {
            return false;
        }

        if (isTransparent && isLayered && !classLooksLikeDesktopShell)
        {
            return false;
        }

        if (!hasCaption && !hasMeasuredTitleBarSafeBounds && !classLooksLikeDesktopShell)
        {
            return false;
        }

        if (!hasSystemMenu && !hasMinimizeOrMaximizeButton && !isAppWindow && !classLooksLikeDesktopShell)
        {
            return false;
        }

        if (clientFillRatio < 0.55 && !classLooksLikeDesktopShell)
        {
            return false;
        }

        if (width < 520 && height < 420 && !titleLooksLikeCodex && !classLooksLikeDesktopShell)
        {
            return false;
        }

        return true;
    }

    private static int CalculateCandidateScore(
        string title,
        string className,
        bool isForeground,
        bool isTitleMissing,
        bool hasMeasuredTitleBarSafeBounds,
        int width,
        int height,
        WindowBounds clientBounds,
        nuint style,
        nuint exStyle)
    {
        var score = 0;
        var hasCaption = (style & WsCaption) == WsCaption;
        var hasSystemMenu = (style & WsSysMenu) != 0;
        var hasMinimizeOrMaximizeButton = (style & (WsMinimizeBox | WsMaximizeBox)) != 0;
        var isAppWindow = (exStyle & WsExAppWindow) != 0;
        var classLooksLikeDesktopShell =
            className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
            || className.Contains("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase)
            || className.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
        var titleLooksLikeCodex =
            title.Contains("Codex", StringComparison.OrdinalIgnoreCase)
            || title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || title.Contains("OpenAI", StringComparison.OrdinalIgnoreCase);

        if (isForeground)
        {
            score += 600;
        }

        if (hasCaption)
        {
            score += 2400;
        }

        if (hasSystemMenu)
        {
            score += 900;
        }

        if (hasMinimizeOrMaximizeButton)
        {
            score += 900;
        }

        if (isAppWindow)
        {
            score += 1000;
        }

        if (classLooksLikeDesktopShell)
        {
            score += 2200;
        }

        if (hasMeasuredTitleBarSafeBounds)
        {
            score += 1500;
        }

        if (titleLooksLikeCodex)
        {
            score += 800;
        }

        if (!isTitleMissing)
        {
            score += 500;
        }

        score += Math.Min(3000, Math.Max(0, clientBounds.Width * clientBounds.Height / 1200));
        score += Math.Min(1000, Math.Max(0, width * height / 2000));

        return score;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(IntPtr hWnd, int msg, IntPtr wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
