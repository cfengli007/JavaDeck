using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JavaDeck.Services;

/// <summary>
/// 纯 Win32 托盘图标（不依赖 System.Drawing / WinForms，避免额外 NuGet 包）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
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
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string exeFileName, int iconIndex);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private NOTIFYICONDATA _data;
    private HwndSource? _source;
    private IntPtr _icon;
    private bool _added;

    public event Action? DoubleClick;
    public event Action? RightClick;

    public string Tooltip { get; set; } = "JavaDeck";

    public bool Create(Window owner, string? iconPath)
    {
        try
        {
            var helper = new WindowInteropHelper(owner);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return false;

            _source = HwndSource.FromHwnd(hwnd);
            if (_source == null) return false;
            _source.AddHook(WndProc);

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                // 多尺寸 ico 直接 LoadImage 偶尔失败，先按小尺寸显式加载
                _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                if (_icon == IntPtr.Zero)
                    _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                if (_icon == IntPtr.Zero)
                    _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                if (_icon == IntPtr.Zero)
                    _icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? "", 0);
            }
            else
            {
                _icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? "", 0);
            }

            if (_icon == IntPtr.Zero) return false;

            _data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_TIP | NIF_ICON,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _icon,
                szTip = Tooltip,
                szInfo = "",
                szInfoTitle = "",
            };

            _added = Shell_NotifyIcon(NIM_ADD, ref _data);
            return _added;
        }
        catch { return false; }
    }

    public void UpdateTooltip(string tip)
    {
        if (!_added) return;
        Tooltip = tip;
        _data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var evt = lParam.ToInt32() & 0xFFFF;
            if (evt == WM_LBUTTONDBLCLK) { DoubleClick?.Invoke(); handled = true; }
            else if (evt == WM_RBUTTONUP) { RightClick?.Invoke(); handled = true; }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
            _source?.RemoveHook(WndProc);
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        }
        catch { }
    }
}