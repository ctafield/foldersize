using System.Globalization;
using System.Text.RegularExpressions;

namespace FolderSize.Core;

public enum RenameRule { FindReplace, Capitalisation, Numbering, ExtractFormat }
public enum RenameCase { Title, Lower, Upper }

public sealed record RenameOptions
{
    public RenameRule Rule { get; init; }
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool IgnoreCase { get; init; } = true;
    public RenameCase Case { get; init; }
    public string Prefix { get; init; } = "File ";
    public string Start { get; init; } = "1";
    public string Padding { get; init; } = "3";
    public string Pattern { get; init; } = @"^(?<text>.*?)(?<number>\d+)$";
    public string Template { get; init; } = "{text}{number:000}";
}

public static class RenameRules
{
    private static readonly Regex Token = new(@"\{(?<key>[A-Za-z_][A-Za-z0-9_]*)(?::(?<format>0{1,12}))?\}",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static Func<string, int, string?> Create(RenameOptions options, CultureInfo culture)
    {
        switch (options.Rule)
        {
            case RenameRule.FindReplace:
                if (string.IsNullOrEmpty(options.Find)) throw new ArgumentException("Enter text to find.");
                return (name, _) => name.Replace(options.Find, options.Replace,
                    options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            case RenameRule.Capitalisation:
                return options.Case switch
                {
                    RenameCase.Title => (name, _) => culture.TextInfo.ToTitleCase(name.ToLower(culture)),
                    RenameCase.Lower => (name, _) => name.ToLower(culture),
                    RenameCase.Upper => (name, _) => name.ToUpper(culture),
                    _ => throw new ArgumentException("Choose a capitalisation rule.")
                };
            case RenameRule.Numbering:
                var (start, padding) = Numbers(options);
                return (_, index) => options.Prefix + checked(start + index).ToString("D" + padding, culture);
            case RenameRule.ExtractFormat:
                var regex = new Regex(options.Pattern,
                    RegexOptions.CultureInvariant | (options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    TimeSpan.FromMilliseconds(100));
                var tokens = Token.Matches(options.Template);
                var remainder = Token.Replace(options.Template, "");
                if (remainder.Contains('{') || remainder.Contains('}'))
                    throw new ArgumentException("Invalid template. Use {name}, {n:000}, or a named group such as {number:00}.");
                var names = regex.GetGroupNames().ToHashSet(StringComparer.Ordinal);
                foreach (Match token in tokens)
                {
                    var key = token.Groups["key"].Value;
                    if (key is not ("name" or "n") && !names.Contains(key))
                        throw new ArgumentException($"The template refers to an unknown group: {key}");
                }
                var first = tokens.Cast<Match>().Any(token => token.Groups["key"].Value == "n")
                    ? Numbers(options).Start : 1;
                return (name, index) =>
                {
                    var match = regex.Match(name);
                    if (!match.Success) return null;
                    return Token.Replace(options.Template, token =>
                    {
                        var key = token.Groups["key"].Value;
                        var value = key switch
                        {
                            "name" => name,
                            "n" => checked(first + index).ToString(CultureInfo.InvariantCulture),
                            _ => match.Groups[key].Success ? match.Groups[key].Value :
                                throw new ArgumentException($"Group '{key}' did not match this file.")
                        };
                        var format = token.Groups["format"].Value;
                        if (format.Length == 0) return value;
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                            throw new ArgumentException($"Group '{key}' must contain a non-negative integer for padding.");
                        return number.ToString(format, CultureInfo.InvariantCulture);
                    });
                };
            default:
                throw new ArgumentException("Choose a rename rule.");
        }
    }

    private static (long Start, int Padding) Numbers(RenameOptions options)
    {
        if (!long.TryParse(options.Start, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 0 ||
            !int.TryParse(options.Padding, out var padding) || padding is < 1 or > 12)
            throw new ArgumentException("Start must be a non-negative integer; padding must be between 1 and 12.");
        return (start, padding);
    }

    public static string? NameError(string originalPath, string proposedName)
    {
        if (string.IsNullOrWhiteSpace(proposedName) || proposedName.Length > 255 ||
            proposedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            proposedName.EndsWith('.') || proposedName.EndsWith(' '))
            return "Invalid Windows filename.";
        var stem = proposedName.Split('.')[0].TrimEnd(' ');
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return "Reserved Windows filename.";
        if (!string.Equals(Path.GetExtension(originalPath), Path.GetExtension(proposedName), StringComparison.OrdinalIgnoreCase))
            return "Keep the original file extension.";
        return null;
    }
}
