namespace FolderSize.Core;

public readonly record struct MapRect(double X, double Y, double Width, double Height);
public sealed record MapItem(ScanNode? Node, long Size, string Label, bool IsRemainder = false);
public sealed record MapTile(MapItem Item, MapRect Bounds);

public static class TreemapLayout
{
    public static IReadOnlyList<MapTile> Create(IEnumerable<ScanNode> nodes, double width, double height) =>
        CreateItems(nodes.Select(node => new MapItem(node, node.Size, node.Name)), width, height);

    public static IReadOnlyList<MapTile> CreateItems(IEnumerable<MapItem> source, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return [];
        var sorted = source.Where(item => item.Size > 0).OrderByDescending(item => item.Size).ToArray();
        if (sorted.Length == 0) return [];
        var total = sorted.Sum(node => (double)node.Size);
        var budget = Math.Clamp((int)Math.Min(width * height / 1500, 1200), 1, 1200);
        var visible = sorted.Take(budget).TakeWhile(node => node.Size / total * width * height >= 16).ToArray();
        var items = visible.ToList();
        if (visible.Length < sorted.Length)
        {
            var remaining = sorted.Skip(visible.Length).Sum(node => node.Size);
            items.Add(new(null, remaining, $"Other ({sorted.Length - visible.Length:N0} entries)", true));
        }
        var scale = width * height / total;
        var result = new List<MapTile>();
        var box = new MapRect(0, 0, width, height);
        var row = new List<MapItem>();
        var index = 0;
        while (index < items.Count)
        {
            var item = items[index];
            var side = Math.Min(box.Width, box.Height);
            if (row.Count == 0 || Worst(row.Append(item), scale, side) <= Worst(row, scale, side))
            {
                row.Add(item);
                index++;
            }
            else
            {
                box = Place(row, box, scale, result);
                row.Clear();
            }
        }
        if (row.Count > 0) Place(row, box, scale, result);
        return result;
    }

    private static double Worst(IEnumerable<MapItem> row, double scale, double side)
    {
        double sum = 0, min = double.MaxValue, max = 0;
        foreach (var item in row)
        {
            var area = item.Size * scale;
            sum += area;
            min = Math.Min(min, area);
            max = Math.Max(max, area);
        }
        var square = side * side;
        return Math.Max(square * max / (sum * sum), sum * sum / (square * min));
    }

    private static MapRect Place(List<MapItem> row, MapRect box, double scale, List<MapTile> tiles)
    {
        var area = row.Sum(item => item.Size * scale);
        var vertical = box.Width >= box.Height;
        var thickness = area / (vertical ? box.Height : box.Width);
        double offset = 0;
        foreach (var item in row)
        {
            var length = item.Size * scale / thickness;
            var bounds = vertical
                ? new MapRect(box.X, box.Y + offset, thickness, length)
                : new MapRect(box.X + offset, box.Y, length, thickness);
            tiles.Add(new(item, bounds));
            offset += length;
        }
        return vertical
            ? new(box.X + thickness, box.Y, Math.Max(0, box.Width - thickness), box.Height)
            : new(box.X, box.Y + thickness, box.Width, Math.Max(0, box.Height - thickness));
    }
}
