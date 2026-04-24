using Cmb.Core.Model.Raw;

namespace Cmb.Io.Csv;

public sealed class HiTessPipeCsvReader
{
    public (IReadOnlyList<RawPipeRow> Rows, IReadOnlyList<ParseSkip> Skips) Read(string path)
    {
        var rows = new List<RawPipeRow>();
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
                skips.Add(new ParseSkip("Pipe", lineNo, name, reason));
                continue;
            }
            rows.Add(row);
        }

        return (rows, skips);
    }

    private static bool TryParse(string[] cols, out RawPipeRow row, out string reason)
    {
        row = default!;
        reason = string.Empty;

        if (cols.Length < PipeCsv.MinColumns)
        {
            reason = $"컬럼 수 부족 ({cols.Length} < {PipeCsv.MinColumns})";
            return false;
        }

        var pos  = CsvParsing.ExtractDoubles(cols[PipeCsv.Pos]);
        var aPos = CsvParsing.ExtractDoubles(cols[PipeCsv.APos]);
        var lPos = CsvParsing.ExtractDoubles(cols[PipeCsv.LPos]);

        if (pos.Length  < 3) { reason = "pos 좌표 3개 미확보";  return false; }
        if (aPos.Length < 3) { reason = "apos 좌표 3개 미확보"; return false; }
        if (lPos.Length < 3) { reason = "lpos 좌표 3개 미확보"; return false; }

        var normal = CsvParsing.ExtractDoubles(cols[PipeCsv.Normal]);
        if (normal.Length < 3) normal = [0.0, 0.0, 0.0];

        var interPos = CsvParsing.ExtractDoublesOrNull(cols[PipeCsv.InterPos]);
        var p3Pos    = CsvParsing.ExtractDoublesOrNull(cols[PipeCsv.P3Pos]);
        var rest     = string.IsNullOrWhiteSpace(cols[PipeCsv.Rest]) ? null : cols[PipeCsv.Rest].Trim();
        var remark   = string.IsNullOrWhiteSpace(cols[PipeCsv.Remark]) ? null : cols[PipeCsv.Remark].Trim();

        row = new RawPipeRow(
            Name:     cols[PipeCsv.Name].Trim(),
            Type:     cols[PipeCsv.Type].Trim().ToUpperInvariant(),
            Branch:   cols[PipeCsv.Branch].Trim(),
            Pos:      pos,
            APos:     aPos,
            LPos:     lPos,
            Normal:   normal,
            InterPos: interPos,
            P3Pos:    p3Pos,
            Rest:     rest,
            OutDia:   CsvParsing.ParseDoubleSafe(cols[PipeCsv.OutDia]),
            Thick:    CsvParsing.ParseDoubleSafe(cols[PipeCsv.Thick]),
            OutDia2:  CsvParsing.ParseDoubleSafe(cols[PipeCsv.OutDia2]),
            Thick2:   CsvParsing.ParseDoubleSafe(cols[PipeCsv.Thick2]),
            Mass:     CsvParsing.ParseDoubleSafe(cols[PipeCsv.Mass]),
            Remark:   remark
        );
        return true;
    }
}
