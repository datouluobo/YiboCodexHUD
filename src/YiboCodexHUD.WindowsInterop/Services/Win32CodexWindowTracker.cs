using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.WindowsInterop.Services;

public sealed partial class Win32CodexWindowTracker : ICodexWindowTracker
{
    private const int WmNcHitTest = 0x0084;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int HtCaption = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRootOwner = 3;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int MinimumTrackedWindowWidth = 320;
    private const int MinimumTrackedWindowHeight = 180;
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

        if (processName.Contains("YiboCodexHUD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var processLooksLikeCodex = processName.Contains("codex", StringComparison.OrdinalIgnoreCase);

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

        var isForeground = windowHandle == foregroundWindow;
        var isTopVisible = IsWindowTopVisible(windowHandle, bounds, clientBounds);
        candidateScore = CalculateCandidateScore(
            processLooksLikeCodex,
            isForeground,
            string.IsNullOrWhiteSpace(title),
            width,
            height,
            clientBounds);

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

    private static int CalculateCandidateScore(
        bool processLooksLikeCodex,
        bool isForeground,
        bool isTitleMissing,
        int width,
        int height,
        WindowBounds clientBounds)
    {
        var score = 0;

        if (isForeground)
        {
            score += 10000;
        }

        if (processLooksLikeCodex)
        {
            score += 3000;
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

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

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
