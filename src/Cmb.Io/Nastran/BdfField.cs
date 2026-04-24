using System.Globalization;

namespace Cmb.Io.Nastran;

internal static class BdfField
{
    // 8-char left-aligned (for keywords like "GRID", "CBEAM")
    public static string L(string s) => Pad(s, false);

    // 8-char right-aligned (for integer IDs and numeric data)
    public static string R(string s)  => Pad(s, true);
    public static string R(int n)     => Pad(n.ToString(CultureInfo.InvariantCulture), true);
    public static string R(double d)  => Pad(FormatDouble(d), true);

    // Nastran short-field scientific notation for very small values (e.g. material density)
    public static string RSci(double d) => Pad(FormatScientific(d), true);

    // ── Formatters ────────────────────────────────────────────────────────────

    private static string FormatDouble(double d)
    {
        if (Math.Abs(d) < 1e-12) return "0.0";

        // Very small or very large → scientific notation
        if (Math.Abs(d) < 0.001 || Math.Abs(d) >= 10_000_000.0)
            return FormatScientific(d);

        // 1 decimal place ("0.0")
        string s = d.ToString("0.0", CultureInfo.InvariantCulture);
        if (s.Length <= 8) return s;

        // Too long → integer representation
        s = d.ToString("0", CultureInfo.InvariantCulture);
        if (s.Length <= 8) return s;

        return FormatScientific(d);
    }

    // Nastran convention: "7.85E-09" → "7.85-9" (no 'E', no leading zero in exponent)
    private static string FormatScientific(double d)
    {
        if (Math.Abs(d) < 1e-12) return "0.0";
        string sci = d.ToString("0.##E+00", CultureInfo.InvariantCulture);
        string[] parts = sci.Split('E');
        return $"{parts[0]}{int.Parse(parts[1], CultureInfo.InvariantCulture)}";
    }

    private static string Pad(string s, bool right)
    {
        if (s.Length > 8) s = s[..8];
        return right ? s.PadLeft(8) : s.PadRight(8);
    }
}
