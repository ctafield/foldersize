namespace FolderSize.Core;

public static class RecycleBinPaths
{
    public static bool IsWithin(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path must have a drive root.", nameof(path));
        var binPath = Path.Combine(root, "$Recycle.Bin");
        return fullPath.Equals(binPath, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(binPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
