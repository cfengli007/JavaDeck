using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JavaDeck.Services;

/// <summary>
/// Windows 11 圆角与边框效果。
/// WindowStyle=None 的窗口默认是直角，需要通过 DWM 属性告诉系统绘制圆角。
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>应用 Windows 11 圆角。低版本系统安全忽略。</summary>
    public static void ApplyRoundedCorners(Window window, bool round = true)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var pref = (int)(round ? DwmWindowCornerPreference.Round : DwmWindowCornerPreference.DoNotRound);
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>设置窗口边框颜色（0xFFFFFFFE = 使用系统默认）。</summary>
    public static void SetBorderColor(Window window, int colorRef)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
        catch { }
    }

    /// <summary>浅色/深色标题栏框架。</summary>
    public static void SetDarkMode(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        catch { }
    }

    /// <summary>设置标题栏颜色（Win11 22000+）。COLORREF 是 0x00BBGGRR。</summary>
    public static void SetCaptionColor(Window window, int colorRef)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }
        catch { }
    }

    /// <summary>设置标题栏文字颜色（Win11 22000+）。</summary>
    public static void SetCaptionTextColor(Window window, int colorRef)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref colorRef, sizeof(int));
        }
        catch { }
    }

    /// <summary>一次性应用：圆角 + 边框颜色 + 浅色模式。</summary>
    public static void Apply(Window window)
    {
        ApplyRoundedCorners(window, true);
        SetDarkMode(window, false);
        // 边框用浅灰，和主题 #E2E5EB 一致（COLORREF 是 0x00BBGGRR）
        SetBorderColor(window, 0x00EBE5E2);
    }
}