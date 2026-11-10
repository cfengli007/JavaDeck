using System.IO;
using Microsoft.Win32;

namespace JavaDeck.Services;

/// <summary>开机自启（写 HKCU\...\Run，不需要管理员）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JavaDeck";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    public static bool Enable(bool enable, bool minimized = false)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (k == null) return false;

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                var args = minimized ? " --minimized" : "";
                k.SetValue(ValueName, "\"" + exe + "\"" + args);
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}
