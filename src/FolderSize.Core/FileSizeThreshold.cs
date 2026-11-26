using System.Globalization;

namespace FolderSize.Core;

public static class FileSizeThreshold
{
    public static long Parse(string value, string unit, CultureInfo culture)
    {
        const NumberStyles style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign |
                                   NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(value, style, culture, out var number) || number < 0)
            throw new ArgumentException("Enter a non-negative number for the minimum size.");
        long multiplier = unit switch
        {
            "kb" => 1L << 10,
            "mb" => 1L << 20,
            "gb" => 1L << 30,
            "tb" => 1L << 40,
            _ => throw new ArgumentException("Choose kb, mb, gb, or tb.")
        };
        if (number > (decimal)long.MaxValue / multiplier)
            throw new ArgumentException("The minimum size is too large.");
        return (long)decimal.Ceiling(number * multiplier);
    }
}
