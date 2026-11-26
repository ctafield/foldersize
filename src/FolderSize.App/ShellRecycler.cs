using System.IO;
using System.Runtime.InteropServices;
using FolderSize.Core;

namespace FolderSize.App;

public sealed class ShellRecycler : IRecycler
{
    public void Recycle(string path)
    {
        WindowsFileAccess.EnsureNoLinks(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Cannot determine the file volume.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Recycling is supported only on local fixed drives. File was not deleted.");
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("Only individual files can be recycled.");

        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)new FileOperation();
            // RECYCLEONDELETE is required; the sink also refuses a non-recycling delete.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x0400 | 0x0040 |
                                        0x0010 | 0x0004 | 0x2000);
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out item));
            var sink = new RecycleSink();
            operation.DeleteItem(item, sink);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || !sink.Recycled)
                throw new IOException(sink.Error ?? "Windows did not confirm recycling. No permanent-delete fallback was used.");
        }
        catch (COMException ex)
        {
            throw new IOException($"Windows could not recycle the file: {ex.Message}", ex);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

[ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
internal class FileOperation;

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint kind, out nint name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(IFileOperationProgressSink sink, out uint cookie);
    void Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    void SetProgressDialog(nint dialog);
    void SetProperties(nint properties);
    void SetOwnerWindow(uint owner);
    void ApplyPropertiesToItem(IShellItem item);
    void ApplyPropertiesToItems(nint items);
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
    void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
    void MoveItems(nint items, IShellItem destination);
    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
    void CopyItems(nint items, IShellItem destination);
    void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
    void DeleteItems(nint items);
    void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
    void PerformOperations();
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
    [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
    [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
    [PreserveSig] int UpdateProgress(uint total, uint completed);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleSink : IFileOperationProgressSink
{
    private const int Abort = unchecked((int)0x80004004);
    public bool Recycled { get; private set; }
    public string? Error { get; private set; }
    public int PreDeleteItem(uint flags, IShellItem item)
    {
        if ((flags & 0x80) != 0) return 0;
        Error = "Windows cannot recycle this file. Permanent deletion was refused.";
        return Abort;
    }
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
    {
        Recycled = result >= 0 && (flags & 0x80) != 0 && created is not null;
        if (!Recycled) Error = $"Recycling was not confirmed (0x{result:X8}). Refresh the scan before retrying.";
        return 0;
    }
    public int StartOperations() => 0;
    public int FinishOperations(int result) => 0;
    public int PreRenameItem(uint flags, IShellItem item, string name) => Abort;
    public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => Abort;
    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => Abort;
    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
    public int PreNewItem(uint flags, IShellItem destination, string name) => Abort;
    public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem? created) => 0;
    public int UpdateProgress(uint total, uint completed) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
