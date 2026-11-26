using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FolderSize.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileAccess : IFileAccess
{
    public bool SupportsConcurrentReads => true;

    public FileLease Open(string path)
    {
        EnsureNoLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.SequentialScan);
        try
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                throw new IOException("Cannot establish file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
            EnsureNoLinks(path);
            var length = ((long)info.SizeHigh << 32) | info.SizeLow;
            var modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
            var identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
            return new(stream, new(length, modified, identity));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links and junctions are not eligible: {current}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}
