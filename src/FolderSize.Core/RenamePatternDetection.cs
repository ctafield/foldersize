using System.Globalization;
using System.Text.RegularExpressions;

namespace FolderSize.Core;

public sealed record RenamePatternSuggestion(string Title, RenameOptions Options, int Matched, int Total,
    int Changed, IReadOnlyList<string> Examples)
{
    public string Summary => $"{Title} - matches {Matched:N0} of {Total:N0} files; {Changed:N0} would change";
}

public static class RenamePatternDetection
{
    private const string Separators = @"[\s_.-]";
    private const string VolumeTail = @"vol(?:ume)?\.?[\s_.-]*(?<volume>[0-9]{1,12})[\s_.-]*(?:no|n|issue)\.?[\s_.-]*(?<issue>[0-9]{1,12})(?<suffix>(?:[^0-9].*)?)";
    private const string NumberTail = @"(?<number>[0-9]{1,12})(?<suffix>[^0-9]*)";
    private const string DateTail = @"(?<year>[0-9]{4})[-_. ](?<month>[0-9]{1,2})[-_. ](?<day>[0-9]{1,2})(?<suffix>(?:[^0-9].*)?)";
    private static readonly Regex Volume = Probe(@"^(?<prefix>.+?)[\s_.-]+" + VolumeTail + "$");
    private static readonly Regex Number = Probe(@"^(?<prefix>[^0-9]*?)" + NumberTail + "$");
    private static readonly Regex Date = Probe(@"^(?<prefix>(?:.*?[\s_.-])?)" + DateTail + "$");
    private static readonly Regex SeparatorRuns = Probe(Separators + "+");

    public static IReadOnlyList<RenamePatternSuggestion> Detect(IReadOnlyList<string> fileNames,
        CultureInfo culture, CancellationToken token = default)
    {
        var files = new List<FileName>(fileNames.Count);
        var volumes = new List<DetectedName>();
        var numbers = new List<DetectedName>();
        var dates = new List<DetectedName>();
        foreach (var name in fileNames)
        {
            token.ThrowIfCancellationRequested();
            var file = new FileName(name, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            files.Add(file);
            Collect(Volume, file, volumes);
            Collect(Number, file, numbers);
            Collect(Date, file, dates);
        }
        var suggestions = new List<(int Priority, RenamePatternSuggestion Suggestion)>();
        foreach (var group in Groups(volumes))
        {
            token.ThrowIfCancellationRequested();
            var prefix = CommonPrefix(group);
            if (!CanUseLiteral(prefix)) continue;
            Add(0, $"Volume and issue: {prefix}", Extract(PrefixPattern(prefix, true) + VolumeTail,
                prefix + " - Vol " + Padded("volume", group) + " - No " + Padded("issue", group) + "{suffix}"),
                group.Select(item => item.File));
        }
        foreach (var group in Groups(dates))
        {
            token.ThrowIfCancellationRequested();
            // Do not guess day/month ordering or suggest a date rule for a group containing invalid dates.
            if (group.Any(item => !ValidDate(item.Match))) continue;
            var prefix = CommonPrefix(group);
            if (!CanUseLiteral(prefix)) continue;
            Add(1, $"Year-first dates{Label(prefix)}", Extract(PrefixPattern(prefix, true) + DateTail,
                (prefix.Length == 0 ? "" : prefix + " - ") + "{year:0000}-{month:00}-{day:00}{suffix}"),
                group.Select(item => item.File));
        }
        foreach (var group in Groups(numbers))
        {
            token.ThrowIfCancellationRequested();
            var prefix = CommonPrefix(group);
            if (!CanUseLiteral(prefix)) continue;
            Add(2, $"Numbered series{Label(prefix)}", Extract(PrefixPattern(prefix, false) + NumberTail,
                (prefix.Length == 0 ? "" : prefix + " ") + Padded("number", group) + "{suffix}"),
                group.Select(item => item.File));
        }
        Add(3, "Replace underscores with spaces", new() { Find = "_", Replace = " " },
            files.Where(file => file.Stem.Contains('_')));
        Add(3, "Replace double spaces with single spaces", new() { Find = "  ", Replace = " " },
            files.Where(file => file.Stem.Contains("  ", StringComparison.Ordinal)));
        Add(4, "Use title case", new() { Rule = RenameRule.Capitalisation, Case = RenameCase.Title }, files);
        token.ThrowIfCancellationRequested();
        return suggestions.OrderBy(item => item.Priority)
            .ThenByDescending(item => item.Suggestion.Matched)
            .ThenByDescending(item => item.Suggestion.Changed)
            .ThenBy(item => item.Suggestion.Title, StringComparer.Ordinal)
            .Take(12).Select(item => item.Suggestion).ToArray();

        void Add(int priority, string title, RenameOptions options, IEnumerable<FileName> members)
        {
            var transform = RenameRules.Create(options, culture);
            var matched = 0;
            var changed = 0;
            var examples = new List<string>(3);
            foreach (var file in members)
            {
                token.ThrowIfCancellationRequested();
                var name = transform(file.Stem, matched);
                if (name is null) continue;
                var proposed = name + file.Extension;
                if (RenameRules.NameError(file.Name, proposed) is not null) return;
                matched++;
                if (proposed == file.Name) continue;
                changed++;
                if (examples.Count < 3) examples.Add($"{file.Name}  ->  {proposed}");
            }
            if (matched >= 2 && changed > 0)
                suggestions.Add((priority, new(title, options, matched, files.Count, changed, examples)));
        }
    }

    private static Regex Probe(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    private static void Collect(Regex regex, FileName file, List<DetectedName> found)
    {
        var match = regex.Match(file.Stem);
        if (match.Success) found.Add(new(file, match));
    }

    private static IEnumerable<IGrouping<string, DetectedName>> Groups(IEnumerable<DetectedName> files) =>
        files.GroupBy(file => NormalPrefix(file.Match.Groups["prefix"].Value), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Take(8);

    private static string NormalPrefix(string prefix) => SeparatorRuns.Replace(prefix, " ").Trim();

    private static string CommonPrefix(IEnumerable<DetectedName> group) => group
        .Select(item => NormalPrefix(item.Match.Groups["prefix"].Value))
        .OrderBy(prefix => prefix.Any(char.IsLower) && prefix.Any(char.IsUpper) ? 0 : 1)
        .ThenBy(prefix => prefix, StringComparer.Ordinal).First();

    private static string PrefixPattern(string prefix, bool separatorRequired) =>
        Separators + "*" + string.Join(Separators + "+", prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) +
        (prefix.Length == 0 ? "" : Separators + (separatorRequired ? "+" : "*"));

    private static string Padded(string key, IEnumerable<DetectedName> group) =>
        "{" + key + ":" + new string('0', Math.Max(2, group.Max(item => item.Match.Groups[key].Length))) + "}";

    private static RenameOptions Extract(string pattern, string template) => new()
    {
        Rule = RenameRule.ExtractFormat, Pattern = "^" + pattern + "$", Template = template
    };

    private static bool ValidDate(Match match) => DateOnly.TryParseExact(
        $"{match.Groups["year"].Value}-{match.Groups["month"].Value}-{match.Groups["day"].Value}",
        ["yyyy-M-d", "yyyy-MM-d", "yyyy-M-dd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool CanUseLiteral(string prefix) => !prefix.Contains('{') && !prefix.Contains('}');
    private static string Label(string prefix) => prefix.Length == 0 ? "" : ": " + prefix;
    private sealed record FileName(string Name, string Stem, string Extension);
    private sealed record DetectedName(FileName File, Match Match);
}
