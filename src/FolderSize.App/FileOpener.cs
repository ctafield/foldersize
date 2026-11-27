using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FolderSize.Core;

namespace FolderSize.App;

public sealed class FileOpener(Action<ProcessStartInfo>? launch = null)
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".dll", ".cpl", ".ocx", ".sys", ".drv", ".efi",
        ".bat", ".cmd", ".ps1", ".ps1xml", ".ps2", ".psc1", ".psc2", ".sh", ".py", ".pyw",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp", ".mst",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".application", ".appref-ms",
        ".lnk", ".url", ".scf", ".reg", ".inf", ".ins", ".isp", ".msc", ".chm",
        ".jar", ".jnlp", ".gadget", ".settingcontent-ms", ".library-ms", ".search-ms",
        ".vb", ".vba", ".vsmacros", ".sct", ".wsc", ".workflow", ".diagcab"
    };

    public void Open(string path)
    {
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        if (BlockedExtensions.Contains(extension) || AssocIsDangerous(extension))
            throw new InvalidOperationException("Executables, scripts, installers, and shortcuts cannot be opened from FolderSize.");
        WindowsFileAccess.EnsureNoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new InvalidOperationException("Only files can be opened with this action.");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadByte() == 'M' && stream.ReadByte() == 'Z')
                throw new InvalidOperationException("Executable file contents were detected. The file was not opened.");
        }
        var info = new ProcessStartInfo { FileName = path, UseShellExecute = true };
        if (launch is not null) launch(info);
        else Process.Start(info);
    }

    public void OpenFolderInExplorer(string path)
    {
        path = FolderPaths.Normalize(path);
        WindowsFileAccess.EnsureNoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
            throw new InvalidOperationException("The path is no longer a folder.");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = false
        };
        info.ArgumentList.Add(path);
        if (launch is not null) launch(info);
        else
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Windows did not start File Explorer.");
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssocIsDangerous(string association);
}
