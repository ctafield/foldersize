using System.IO;
using System.Runtime.InteropServices;

namespace FolderSize.Tests;

public sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FolderSize-tests-" + Guid.NewGuid().ToString("N"));
    public TestFolder() => Directory.CreateDirectory(Root);
    public string Write(string relative, string contents)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string link, string existing, nint security);
}
