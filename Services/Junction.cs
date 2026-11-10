using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JavaDeck.Services;

/// <summary>创建 NTFS 目录联接（junction）。不需要管理员权限。</summary>
public static class Junction
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer,
        int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>沿着 reparse point 链一路解析到真实目录（scoop 的 current 就是二级链接）。</summary>
    public static string ResolveTarget(string path)
    {
        var current = Path.GetFullPath(path);
        for (var depth = 0; depth < 8; depth++)
        {
            DirectoryInfo di;
            try { di = new DirectoryInfo(current); } catch { return current; }
            if (!di.Exists) return current;
            if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return current;
            var target = di.LinkTarget;
            if (string.IsNullOrEmpty(target)) return current;
            current = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(di.Parent!.FullName, target));
        }
        return current;
    }

    public static void Create(string linkPath, string targetPath)
    {
        var real = ResolveTarget(targetPath);
        if (Directory.Exists(linkPath)) Directory.Delete(linkPath, false);
        Directory.CreateDirectory(linkPath);

        var substitute = @"\??\" + real;
        var printName = real;
        var subBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        var data = new byte[8 + 8 + subBytes.Length + 2 + printBytes.Length + 2];
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(8 + subBytes.Length + 2 + printBytes.Length + 2)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)subBytes.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(subBytes.Length + 2)).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(data, 14);
        subBytes.CopyTo(data, 16);
        printBytes.CopyTo(data, 16 + subBytes.Length + 2);

        using var h = CreateFileW(linkPath, GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"打开链接失败 {linkPath}: Win32 {Marshal.GetLastWin32Error()}");

        if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"创建联接失败 {linkPath} -> {real}: Win32 {Marshal.GetLastWin32Error()}");
    }
}
