using Cmb.Core.Model.Raw;

namespace Cmb.Io.Csv;

public sealed class HiTessStructureCsvReader
{
    public (IReadOnlyList<RawBeamRow> Rows, IReadOnlyList<ParseSkip> Skips) Read(string path)
    {
        var rows = new List<RawBeamRow>();
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
                skips.Add(new ParseSkip("Structure", lineNo, name, reason));
                continue;
            }
            rows.Add(row);
        }

        return (rows, skips);
    }

    private static bool TryParse(string[] cols, out RawBeamRow row, out string reason)
    {
        row = default!;
        reason = string.Empty;

        if (cols.Length < StructureCsv.MinColumns)
        {
            reason = $"컬럼 수 부족 ({cols.Length} < {StructureCsv.MinColumns})";
            return false;
        }

        var (sectionType, dims) = CsvParsing.ExtractTypeAndDims(cols[StructureCsv.Size].Trim());
        var startPos = CsvParsing.ExtractDoubles(cols[StructureCsv.StartPos]);
        var endPos   = CsvParsing.ExtractDoubles(cols[StructureCsv.EndPos]);
        var ori      = CsvParsing.ExtractDoubles(cols[StructureCsv.Ori]);

        if (startPos.Length < 3) { reason = "시작점 좌표 3개 미확보"; return false; }
        if (endPos.Length < 3)   { reason = "끝점 좌표 3개 미확보";   return false; }
        if (ori.Length < 3)      { reason = "방향벡터 3개 미확보";     return false; }

        var weld = cols.Length > StructureCsv.Weld
            ? cols[StructureCsv.Weld].Trim().ToLowerInvariant()
            : string.Empty;

        row = new RawBeamRow(
            Name:        cols[StructureCsv.Name].Trim(),
            SectionType: sectionType,
            SizeRaw:     cols[StructureCsv.Size].Trim(),
            Dims:        dims,
            StartPos:    startPos,
            EndPos:      endPos,
            Ori:         ori,
            Weld:        weld
        );
        return true;
    }
}
