namespace FolderSize.Core;

public sealed record SpaceMapGroups(IReadOnlyList<MapItem> Extensions, IReadOnlyList<MapItem> Kinds)
{
    private static readonly Dictionary<string, string> Categories = BuildCategories();

    public static SpaceMapGroups Create(ScanNode root)
    {
        var extensions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in root.Files())
        {
            if (file.Size == 0) continue;
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            var label = extension.Length == 0 ? "No extension" : extension;
            var kind = extension.Length == 0 ? "No extension" : Categories.GetValueOrDefault(extension, "Other");
            extensions[label] = extensions.GetValueOrDefault(label) + file.Size;
            kinds[kind] = kinds.GetValueOrDefault(kind) + file.Size;
        }
        return new(ToItems(extensions), ToItems(kinds));
    }

    private static MapItem[] ToItems(Dictionary<string, long> totals) =>
        totals.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MapItem(null, pair.Value, pair.Key)).ToArray();

    private static Dictionary<string, string> BuildCategories()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("Images", ".jpg .jpeg .png .gif .bmp .webp .svg .ico .tif .tiff .heic .heif .avif .raw");
        Add("Videos", ".mp4 .mkv .avi .mov .wmv .webm .m4v .mpg .mpeg .m2ts .3gp");
        Add("Audio", ".mp3 .wav .flac .aac .ogg .m4a .wma .opus .aiff");
        Add("Documents", ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .rtf .odt .ods .odp .csv .epub .md");
        Add("Archives", ".zip .7z .rar .tar .gz .bz2 .xz .iso .cab .zst");
        Add("Apps & libraries", ".exe .dll .msi .msix .appx .com .sys");
        Add("Code & data", ".cs .xaml .json .xml .yaml .yml .html .css .js .ts .tsx .jsx .py .cpp .c .h .go .rs .sql .db .sqlite .ps1 .bat .sh");
        return result;

        void Add(string kind, string extensions)
        {
            foreach (var extension in extensions.Split(' ')) result.Add(extension, kind);
        }
    }
}
