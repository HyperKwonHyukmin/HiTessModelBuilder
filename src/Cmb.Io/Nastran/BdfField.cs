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
    public static string RMass(double d) => Pad(FormatPrecise(d), true);
    // 단면 치수 등 정밀도가 중요한 실수 — 8자 안에서 유효숫자를 최대로 쓴다
    public static string RPrecise(double d) => Pad(FormatPrecise(d), true);
    public static string ROrientation(double d) => Pad(FormatOrientation(d), true);

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

    private static string FormatOrientation(double d)
    {
        if (Math.Abs(d) < 1e-12) return "0.0";
        if (Math.Abs(d - 1.0) < 1e-12) return "1.0";
        if (Math.Abs(d + 1.0) < 1e-12) return "-1.0";

        var s = d.ToString("0.#####", CultureInfo.InvariantCulture);
        if (s is "0" or "-0") return "0.0";
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
            s += ".0";
        return s.Length <= 8 ? s : FormatScientific(d);
    }

    // 8자 필드 안에서 유효숫자를 최대로 쓴다.
    // 과거 질량 "0.###"(t) 은 kg 단위 반올림이라 1~10 kg 질량이 최대 ±33% 틀렸고(0.0015 → 0.002),
    // 단면 치수 "0.0" 은 얇은 배관 두께를 0.1 mm 로 반올림해 단면적이 수 % 어긋났다(R_in 33.45 → 33.5).
    private static string FormatPrecise(double d)
    {
        if (Math.Abs(d) < 1e-12) return "0.0";
        if (Math.Abs(d) >= 0.001 && Math.Abs(d) < 10_000_000.0)
        {
            for (int decimals = 6; decimals >= 1; decimals--)
            {
                string s = d.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
                if (!s.Contains('.')) s += ".0";
                if (s.Length <= 8) return s;
            }
        }
        for (int digits = 4; digits >= 1; digits--)
        {
            string sci = d.ToString("0." + new string('#', digits) + "E+00", CultureInfo.InvariantCulture);
            string[] parts = sci.Split('E');
            string mant = parts[0].Contains('.') ? parts[0] : parts[0] + ".";
            string s = $"{mant}{int.Parse(parts[1], CultureInfo.InvariantCulture)}";
            if (s.Length <= 8) return s;
        }
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
