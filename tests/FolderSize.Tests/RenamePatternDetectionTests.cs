using System.Globalization;
using FolderSize.Core;

namespace FolderSize.Tests;

public sealed class RenamePatternDetectionTests
{
    [Fact]
    public void VolumeIssueSuggestionsHandleVariantsAndKeepDescriptionsAndExtensions()
    {
        string[] names = ["Escort Vol.33 No.1 - January 1998.pdf", "ESCORT VOL 33 N02 - bonus.PDF", "unrelated.txt"];
        var suggestions = Detect(names);
        var suggestion = Assert.Single(suggestions, item => item.Title.StartsWith("Volume and issue:", StringComparison.Ordinal));
        Assert.Equal(2, suggestion.Matched);
        Assert.Equal(3, suggestion.Total);
        Assert.Equal(2, suggestion.Changed);
        Assert.Equal("Escort - Vol 33 - No 01 - January 1998.pdf", Transform(suggestion, names[0]));
        Assert.Equal("Escort - Vol 33 - No 02 - bonus.PDF", Transform(suggestion, names[1]));
        Assert.Null(Transform(suggestion, names[2]));
        Assert.Contains(suggestion.Examples, example => example.Contains("January 1998", StringComparison.Ordinal));
        Assert.Equal(suggestion, suggestions[0]);
    }

    [Fact]
    public void NumberedSeriesAreGroupedByPrefixAndKeepExistingNumbersAndSuffixes()
    {
        string[] names = ["Photo_001-summer.JPG", "photo 2-winter.JPG", "Invoice_7.txt", "Invoice_8.txt", "other.txt"];
        var suggestions = Detect(names);
        var photo = Assert.Single(suggestions, item => item.Title == "Numbered series: Photo");
        var invoice = Assert.Single(suggestions, item => item.Title == "Numbered series: Invoice");
        Assert.Equal(2, photo.Matched);
        Assert.Equal(5, photo.Total);
        Assert.Equal("Photo 001-summer.JPG", Transform(photo, names[0]));
        Assert.Equal("Photo 002-winter.JPG", Transform(photo, names[1]));
        Assert.Null(Transform(photo, names[2]));
        Assert.Equal("Invoice 07.txt", Transform(invoice, names[2]));
    }

    [Fact]
    public void YearFirstDatesKeepDescriptionsAndAmbiguousDayMonthNamesDoNotMatch()
    {
        string[] names = ["Report_2024_2_29 - draft.pdf", "Report 2026.11.3 - final.pdf", "Report_03-11-2026.pdf"];
        var suggestion = Assert.Single(Detect(names), item => item.Title.StartsWith("Year-first dates", StringComparison.Ordinal));
        Assert.Equal(2, suggestion.Matched);
        Assert.Equal("Report - 2024-02-29 - draft.pdf", Transform(suggestion, names[0]));
        Assert.Equal("Report - 2026-11-03 - final.pdf", Transform(suggestion, names[1]));
        Assert.Null(Transform(suggestion, names[2]));
        Assert.DoesNotContain(Detect(["Report_2025_2_29.pdf", "Report_2026_11_3.pdf"]),
            item => item.Title.StartsWith("Year-first dates", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupSuggestionsKeepEveryWordAndDoNotTouchExtensions()
    {
        string[] names = ["MY_REPORT - draft.TXT", "MY_NOTES - final.TXT"];
        var suggestions = Detect(names);
        var separators = Assert.Single(suggestions, item => item.Title == "Replace underscores with spaces");
        Assert.Equal("MY REPORT - draft.TXT", Transform(separators, names[0]));
        var casing = Assert.Single(suggestions, item => item.Title == "Use title case");
        Assert.Equal("My_Report - Draft.TXT", Transform(casing, names[0]));
    }

    [Fact]
    public void RequiresRepeatedEvidenceAndAChangeAndHonoursCancellation()
    {
        Assert.Empty(Detect([]));
        Assert.Empty(Detect(["single_1.txt"]));
        Assert.Empty(Detect(["Photo 01.txt", "Photo 02.txt"]));
        Assert.Empty(Detect(["Photo 1234567890123.txt", "Photo 1234567890124.txt"]));
        Assert.Throws<OperationCanceledException>(() =>
            RenamePatternDetection.Detect(["Photo1.txt", "Photo2.txt"], CultureInfo.InvariantCulture, new CancellationToken(true)));
    }

    private static IReadOnlyList<RenamePatternSuggestion> Detect(string[] names) =>
        RenamePatternDetection.Detect(names, CultureInfo.InvariantCulture);

    private static string? Transform(RenamePatternSuggestion suggestion, string fileName)
    {
        var name = RenameRules.Create(suggestion.Options, CultureInfo.InvariantCulture)(System.IO.Path.GetFileNameWithoutExtension(fileName), 0);
        return name is null ? null : name + System.IO.Path.GetExtension(fileName);
    }
}
