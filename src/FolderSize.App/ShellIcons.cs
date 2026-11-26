using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FolderSize.App;

public static class ShellIcons
{
    private static readonly Dictionary<string, Task<BitmapSource>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<Action> Pending = new();

    static ShellIcons()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Pending.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "FolderSize shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<BitmapSource> GetAsync(string name, bool directory)
    {
        var extension = Path.GetExtension(name);
        var key = directory ? "folder" : "file:" + extension;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Cache.Count >= 256) Cache.Remove(Cache.Keys.First());
            Cache.Add(key, completion.Task);
            Pending.Add(() =>
            {
                try { completion.SetResult(Load(directory ? "folder" : "file" + extension, directory)); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }
    }

    private static BitmapSource Load(string name, bool directory)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try
        {
            // USEFILEATTRIBUTES gets the associated type icon without opening the actual file.
            var result = SHGetFileInfo(name, directory ? 0x10u : 0x80u, out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x1 | 0x10);
            if (result == 0 || info.Icon == 0)
                throw new IOException($"Windows could not load the associated icon for {name}.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        finally { OleUninitialize(); }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
}
