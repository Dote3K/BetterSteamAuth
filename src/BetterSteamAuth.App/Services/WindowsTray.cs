using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BetterSteamAuth.App.Services;

/// <summary>The app's icon in the notification area, and the notifications it shows.</summary>
public interface ITrayIcon
{
    /// <summary>Shows a Windows notification. <paramref name="onClick"/> runs if the user clicks it.</summary>
    void Notify(string title, string text, Action? onClick = null);
}

/// <summary>
/// A notification-area icon through <c>Shell_NotifyIcon</c>, with a small menu. Its balloons are what Windows 10 and
/// 11 show as toast notifications, credited to the app, so no WinRT projection or AppUserModelID registration is
/// needed. Messages arrive through a message-only window on the UI thread, which Avalonia's message loop dispatches.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTray : ITrayIcon, IDisposable
{
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202;
    private const int WmNull = 0x0000;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const int NinBalloonUserClick = 0x0405;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NimSetVersion = 4;
    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;
    private const int NifShowTip = 0x80;
    private const int NotifyIconVersion4 = 4;
    private const int NiifUser = 0x04;
    private const int NiifLargeIcon = 0x20;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly WndProc _wndProc;
    private readonly string _className = "BetterSteamAuth.Tray." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly IntPtr _largeIcon;
    private readonly int _taskbarCreated;
    private readonly string _tooltip;
    private readonly Func<IReadOnlyList<(string Label, Action Run)>> _menuSource;
    private readonly Action _open;
    private Action? _balloonClick;
    private bool _disposed;

    /// <param name="open">Clicking the icon.</param>
    /// <param name="menu">Right-click menu items, in order; built each time it opens.</param>
    public WindowsTray(string tooltip, Action open, Func<IReadOnlyList<(string Label, Action Run)>> menu)
    {
        _tooltip = tooltip;
        _open = open;
        _menuSource = menu;
        _wndProc = OnMessage;
        var windowClass = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception();
        }

        _window = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        // The exe's own icon: small for the notification area, large for notifications.
        IntPtr[] large = [IntPtr.Zero];
        IntPtr[] small = [IntPtr.Zero];
        _ = ExtractIconExW(Environment.ProcessPath ?? "", 0, large, small, 1);
        _icon = small[0];
        _largeIcon = large[0];
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    public void Notify(string title, string text, Action? onClick = null)
    {
        if (_disposed)
        {
            return;
        }

        _balloonClick = onClick;
        var data = NewData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = _largeIcon != IntPtr.Zero ? NiifUser | NiifLargeIcon : 0;
        data.hBalloonIcon = _largeIcon;
        _ = ShellNotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = NewData(0);
        _ = ShellNotifyIconW(NimDelete, ref data);
        _ = DestroyWindow(_window);
        _ = UnregisterClassW(_className, GetModuleHandleW(null));
        foreach (var icon in new[] { _icon, _largeIcon }.Where(i => i != IntPtr.Zero))
        {
            _ = DestroyIcon(icon);
        }
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 127);
        _ = ShellNotifyIconW(NimAdd, ref data);
        data.uVersionOrTimeout = NotifyIconVersion4;
        _ = ShellNotifyIconW(NimSetVersion, ref data);
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp or NinSelect or NinKeySelect:
                    _open();
                    break;
                case WmContextMenu:
                    ShowMenu();
                    break;
                case NinBalloonUserClick:
                    (_balloonClick ?? _open)();
                    _balloonClick = null;
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == _taskbarCreated)
        {
            // Explorer restarted: the icon has to be added again.
            Add();
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = _menuSource();
        IntPtr menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                _ = AppendMenuW(menu, 0, (UIntPtr)(uint)(i + 1), items[i].Label);
            }

            _ = GetCursorPos(out var point);

            // Without this the menu doesn't close when clicking elsewhere.
            _ = SetForegroundWindow(_window);
            int chosen = TrackPopupMenuEx(menu, 0x0100 | 0x0080 | 0x0002, point.X, point.Y, _window, IntPtr.Zero);
            _ = PostMessageW(_window, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count)
            {
                items[chosen - 1].Run();
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private NotifyIconData NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = 1,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // Structs with strings need the built-in marshaller.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, int flags, UIntPtr id, string text);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
}
