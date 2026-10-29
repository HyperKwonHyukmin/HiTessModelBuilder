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
            // 물리적 데이터 행 번호(공백 행 포함)로 센다 — InputAudit 의 dataRowNumber 와 일치시켜
            // 내부 공백 행이 있어도 skip 사유 매칭이 어긋나지 않게 한다(C-2).
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;

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
        var interPos = CsvParsing.ExtractDoublesOrNull(cols[EquipCsv.InterPos]);

        if (pos.Length < 3) { reason = "pos 좌표 3개 미확보"; return false; }
        if (cog.Length < 3) { reason = "cog 좌표 3개 미확보"; return false; }

        row = new RawEquipRow(
            Name: cols[EquipCsv.Name].Trim(),
            Pos:  pos,
            Cog:  cog,
            Mass: CsvParsing.ParseDoubleSafe(cols[EquipCsv.Mass]),
            InterPos: interPos,
            Wvol: cols.Length > EquipCsv.Wvol ? CsvParsing.ParseDoubleSafe(cols[EquipCsv.Wvol]) : 0.0
        );
        return true;
    }
}
