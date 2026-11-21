using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// The island's icon in the taskbar's hidden icons. Click opens the Settings app; right-click shows
/// Settings / Hide island / Quit. Messages arrive through the island window's existing subclass, so no extra window.
/// </summary>
sealed class Tray : IDisposable
{
    const uint WM_APP_TRAY = 0x8000 + 41, WM_SETTINGCHANGE = 0x1A, WM_NULL = 0;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B;
    const int CmdSettings = 1, CmdToggle = 2, CmdQuit = 3;

    readonly nint _hwnd;
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    nint _icon;
    bool _added;

    public event Action? OpenSettings, Quit;

    public Tray(nint hwnd, WinUIEx.Messaging.WindowMessageMonitor messages)
    {
        _hwnd = hwnd;
        messages.WindowMessageReceived += (_, e) =>
        {
            uint msg = e.Message.MessageId;
            if (msg == WM_APP_TRAY) { OnTray((uint)(unchecked((ulong)e.Message.LParam) & 0xFFFF), unchecked((ulong)e.Message.WParam)); e.Handled = true; }
            else if (msg == _taskbarCreated) { _added = false; Add(); } // Explorer restarted: the icon is gone, put it back
            else if (msg == WM_SETTINGCHANGE) Refresh(); // taskbar went light or dark
        };
        Add();
    }

    void OnTray(uint ev, ulong anchor)
    {
        try
        {
            if (ev is NIN_SELECT or NIN_KEYSELECT) OpenSettings?.Invoke();
            else if (ev == WM_CONTEXTMENU) ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
        }
        catch (Exception ex) { Crash.Log("Tray", ex); }
    }

    void ShowMenu(int x, int y)
    {
        nint menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdSettings, "Settings");
        AppendMenu(menu, 0, CmdToggle, Settings.Current.ShowIsland ? "Hide island" : "Show island");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdQuit, "Quit Dynamic Island");
        SetMenuDefaultItem(menu, CmdSettings, 0);

        // The island window never activates (WS_EX_NOACTIVATE), but a tray menu only closes on an outside click when its
        // owner is the foreground window. Let it activate just for the menu.
        nint ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_NOACTIVATE);
        Native.SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */ | 0x20 /* TPM_BOTTOMALIGN */, x, y, _hwnd, 0);
        PostMessage(_hwnd, WM_NULL, 0, 0);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, ex);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdSettings: OpenSettings?.Invoke(); break;
            case CmdToggle: Settings.Update(s => s.ShowIsland = !s.ShowIsland); break;
            case CmdQuit: Quit?.Invoke(); break;
        }
    }

    NOTIFYICONDATA Data()
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_APP_TRAY, hIcon = _icon,
            szTip = "Dynamic Island",
        };
        return d;
    }

    void LoadIcon()
    {
        bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", light ? "TrayLight.ico" : "TrayDark.ico");
        int size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, Native.GetDpiForWindow(_hwnd));
        if (_icon != 0) DestroyIcon(_icon);
        _icon = LoadImage(0, file, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
    }

    void Add()
    {
        LoadIcon();
        var d = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        d.uVersion = 4; // NOTIFYICON_VERSION_4: NIN_SELECT on click, WM_CONTEXTMENU with the anchor point
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    void Refresh()
    {
        if (!_added) return;
        LoadIcon();
        var d = Data();
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        if (_icon != 0) DestroyIcon(_icon);
        _icon = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] static extern nint LoadImage(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPos);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
}
