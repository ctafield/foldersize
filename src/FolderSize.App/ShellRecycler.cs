using System.IO;
using System.Runtime.InteropServices;
using FolderSize.Core;

namespace FolderSize.App;

public sealed class ShellRecycler(bool allowDirectories = false, bool permanentlyDelete = false) : IRecycler
{
    public void Recycle(string path)
    {
        WindowsFileAccess.EnsureNoLinks(path);
        EnsureSupportedDrive(GetDriveType(path), permanentlyDelete);
        if (!allowDirectories && (File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("Only individual files can be removed.");

        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)new FileOperation();
            // Permanent deletion must omit both RECYCLEONDELETE and ALLOWUNDO.
            operation.SetOperationFlags(0x00100000 | 0x0400 | 0x0010 | 0x0004 | 0x2000 |
                                        (permanentlyDelete ? 0u : 0x00080000u | 0x0040u));
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out item));
            var sink = new RecycleSink(permanentlyDelete);
            operation.DeleteItem(item, sink);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || sink.Error is not null || !(permanentlyDelete ? sink.Deleted : sink.Recycled))
                throw new IOException(sink.Error ?? (permanentlyDelete
                    ? "Windows did not confirm permanent deletion. Refresh the scan before retrying."
                    : "Windows did not confirm recycling. No permanent-delete fallback was used."));
            // Positive Shell statuses can describe traversal rather than completed removal.
            if (permanentlyDelete) EnsureRemoved(path);
        }
        catch (COMException ex)
        {
            throw new IOException($"Windows could not {(permanentlyDelete ? "permanently delete" : "recycle")} the item: {ex.Message}", ex);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
        }
    }

    internal static bool DefaultPermanentDeletion(IReadOnlyList<DriveType> driveTypes) =>
        driveTypes.Count > 0 && driveTypes.All(type => type is DriveType.Removable or DriveType.Network);

    internal static void EnsureRemoved(string path)
    {
        try { File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("Windows did not remove the item. Refresh the scan before retrying.");
    }

    internal static DriveType GetDriveType(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Cannot determine the item volume.");
        return GetDriveTypeW(root);
    }

    internal static void EnsureSupportedDrive(DriveType driveType, bool permanentlyDelete = false)
    {
        if (driveType is DriveType.Fixed or DriveType.Removable || permanentlyDelete && driveType == DriveType.Network) return;
        throw new IOException(permanentlyDelete
            ? "Permanent deletion is supported only on local fixed, removable or network drives. Item was not deleted."
            : "Recycling is supported only on local fixed or removable drives. Item was not deleted.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern DriveType GetDriveTypeW(string root);

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
public sealed class RecycleSink(bool permanentlyDelete = false) : IFileOperationProgressSink
{
    private const int Abort = unchecked((int)0x80004004);
    public bool Recycled { get; private set; }
    public bool Deleted { get; private set; }
    public string? Error { get; private set; }
    public int PreDeleteItem(uint flags, IShellItem item)
    {
        var recycling = (flags & 0x80) != 0;
        if (permanentlyDelete ? !recycling : recycling) return 0;
        Error = permanentlyDelete
            ? "Windows requested recycling instead of the confirmed permanent deletion. Operation was refused."
            : "Windows cannot recycle this item. Permanent deletion was refused.";
        return Abort;
    }
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
    {
        Recycled = !permanentlyDelete && result >= 0 && (flags & 0x80) != 0 && created is not null;
        Deleted = permanentlyDelete && result >= 0 && (flags & 0x80) == 0 && created is null;
        if (!(permanentlyDelete ? Deleted : Recycled))
            Error = $"{(permanentlyDelete ? "Permanent deletion" : "Recycling")} was not confirmed (0x{result:X8}). Refresh the scan before retrying.";
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
