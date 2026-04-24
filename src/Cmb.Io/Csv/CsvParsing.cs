using System.Globalization;
using System.Text.RegularExpressions;

namespace Cmb.Io.Csv;

internal static class CsvParsing
{
    private static readonly Regex NumRegex =
        new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex SizeTypeRegex =
        new(@"^(?<type>[A-Z]+)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static double[] ExtractDoubles(string? s)
        => NumRegex.Matches(s ?? "")
                   .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                   .ToArray();

    public static double[]? ExtractDoublesOrNull(string? s)
    {
        var arr = ExtractDoubles(s);
        return arr.Length > 0 ? arr : null;
    }

    public static (string typeUpper, double[] dims) ExtractTypeAndDims(string sizeText)
    {
        var upper = (sizeText ?? "").Trim().ToUpperInvariant();
        var m = SizeTypeRegex.Match(upper);
        var type = m.Success ? m.Groups["type"].Value : "UNKNOWN";
        var dims = NumRegex.Matches(upper)
                           .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture))
                           .ToArray();
        return (type, dims);
    }

    public static double ParseDoubleSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0.0;
        return double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result : 0.0;
    }
}
