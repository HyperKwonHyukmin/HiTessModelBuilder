using Cmb.Core.Model.Raw;

namespace Cmb.Io.Csv;

public sealed class HiTessEquipCsvReader
{
    public (IReadOnlyList<RawEquipRow> Rows, IReadOnlyList<ParseSkip> Skips) Read(string path)
    {
        var rows = new List<RawEquipRow>();
        var skips = new List<ParseSkip>();
        int lineNo = 0;

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lineNo++;

            var cols = line.Split(',');
            var name = cols[0].Trim();

            if (!TryParse(cols, out var row, out var reason))
            {
                skips.Add(new ParseSkip("Equipment", lineNo, name, reason));
                continue;
            }
            rows.Add(row);
        }

        return (rows, skips);
    }

    private static bool TryParse(string[] cols, out RawEquipRow row, out string reason)
    {
        row = default!;
        reason = string.Empty;

        if (cols.Length < EquipCsv.MinColumns)
        {
            reason = $"컬럼 수 부족 ({cols.Length} < {EquipCsv.MinColumns})";
            return false;
        }

        var pos = CsvParsing.ExtractDoubles(cols[EquipCsv.Pos]);
        var cog = CsvParsing.ExtractDoubles(cols[EquipCsv.Cog]);

        if (pos.Length < 3) { reason = "pos 좌표 3개 미확보"; return false; }
        if (cog.Length < 3) { reason = "cog 좌표 3개 미확보"; return false; }

        row = new RawEquipRow(
            Name: cols[EquipCsv.Name].Trim(),
            Pos:  pos,
            Cog:  cog,
            Mass: CsvParsing.ParseDoubleSafe(cols[EquipCsv.Mass])
        );
        return true;
    }
}
