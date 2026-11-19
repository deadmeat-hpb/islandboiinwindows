using System.Runtime.InteropServices;

namespace DynamicIsland;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const uint WM_NCCALCSIZE = 0x83;
    public const nint WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    public const nint WS_EX_TOPMOST = 0x8, HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(nint hwnd, nint hrgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state); // 2 busy/fullscreen, 3 D3D fullscreen, 4 presentation
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetApplicationUserModelId(nint process, ref uint length, System.Text.StringBuilder? id);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] public static extern bool GetMonitorInfo(nint hmon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterApplicationRestart(string? args, uint flags); // relaunch after a crash or hang
    public const nint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    public const uint LWA_ALPHA = 0x2;
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? cls, string? title);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
