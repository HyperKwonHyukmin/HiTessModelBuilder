// M-1 리팩터: Program.cs 에서 분리한 입력 감사(00_InputAudit.json) DTO/분류/직렬화. 동작 불변.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmb.Core.Model.Raw;

internal sealed class InputAuditDocument
{
    [JsonPropertyName("_keys")] public Dictionary<string, string> Keys { get; set; } = InputAuditJson.SchemaKeys;
    [JsonPropertyName("meta")] public InputAuditMeta Meta { get; set; } = new();
    [JsonPropertyName("inputFiles")] public List<InputAuditFile> InputFiles { get; set; } = [];
    [JsonPropertyName("summary")] public InputAuditSummary Summary { get; set; } = new();
    [JsonPropertyName("rowAudit")] public List<InputAuditRow> RowAudit { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, InputAuditJson.Options);
}

internal sealed class InputAuditMeta
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } =
        "Complete CSV input audit: every physical CSV data row is listed with parse/conversion status and reason.";
}

internal sealed class InputAuditFile
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("header")] public List<string> Header { get; set; } = [];
    [JsonPropertyName("physicalLineCount")] public int PhysicalLineCount { get; set; }
    [JsonPropertyName("dataRowCount")] public int DataRowCount { get; set; }
    [JsonPropertyName("blankDataRowCount")] public int BlankDataRowCount { get; set; }
}

internal sealed class InputAuditSummary
{
    [JsonPropertyName("totalDataRows")] public int TotalDataRows { get; set; }
    [JsonPropertyName("convertedRows")] public int ConvertedRows { get; set; }
    [JsonPropertyName("ignoredRows")] public int IgnoredRows { get; set; }
    [JsonPropertyName("errorRows")] public int ErrorRows { get; set; }
    [JsonPropertyName("parseFailedRows")] public int ParseFailedRows { get; set; }
    [JsonPropertyName("blankRows")] public int BlankRows { get; set; }
    [JsonPropertyName("ambiguousDuplicateSourceNameRows")] public int AmbiguousDuplicateSourceNameRows { get; set; }
    [JsonPropertyName("ignoredByReason")] public Dictionary<string, int> IgnoredByReason { get; set; } = [];
    // F-8(2): 좌표 스케일 의심(meter 로 입력된 듯 범위가 지나치게 작음) 경고. 정상이면 생략된다.
    [JsonPropertyName("coordinateScaleWarning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoordinateScaleWarning { get; set; }
}

internal sealed class InputAuditRow
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("physicalLineNumber")] public int PhysicalLineNumber { get; set; }
    [JsonPropertyName("dataRowNumber")] public int DataRowNumber { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("mappingConfidence")] public string MappingConfidence { get; set; } = "sourceName";
    [JsonPropertyName("rawLine")] public string RawLine { get; set; } = "";
    [JsonPropertyName("rawFields")] public Dictionary<string, string> RawFields { get; set; } = [];
}

internal static class InputAuditJson
{
    internal static readonly Dictionary<string, string> SchemaKeys = new()
    {
        ["meta"] = "입력 감사 메타 정보 (schemaVersion, timestamp, unit, purpose)",
        ["inputFiles"] = "입력 CSV 파일 목록과 파일별 행 수/header 정보 (kind, path, exists, header, physicalLineCount, dataRowCount, blankDataRowCount)",
        ["summary"] = "전체 CSV 행 처리 요약 (totalDataRows, convertedRows, ignoredRows, errorRows, parseFailedRows, blankRows, ambiguousDuplicateSourceNameRows, ignoredByReason)",
        ["rowAudit"] = "모든 CSV 데이터 행 감사 배열. CSV 비헤더 행마다 하나씩 기록되며 rawLine/rawFields/status/reason을 포함",
        ["rowAudit.kind"] = "CSV 종류: Structure, Pipe, Equipment",
        ["rowAudit.file"] = "해당 행이 나온 원본 CSV 파일 경로",
        ["rowAudit.physicalLineNumber"] = "CSV 파일 내 실제 줄 번호. 헤더는 1번, 첫 데이터 행은 2번",
        ["rowAudit.dataRowNumber"] = "헤더를 제외한 데이터 행 번호. 첫 데이터 행은 1번",
        ["rowAudit.name"] = "CSV name 컬럼 값",
        ["rowAudit.status"] = "행 처리 상태: converted(CSV 변환 대상으로 수용됨), ignored(의도적으로 제외됨), error(입력 데이터 오류 — 파싱은 됐으나 변환 시 형상 누락 등 문제 발생, 예: 배관 outDia<=0), parseFailed(파싱 실패), blank(공백 행)",
        ["rowAudit.reasonCode"] = "처리 상태를 기계적으로 분류하기 위한 사유 코드",
        ["rowAudit.reason"] = "사용자가 읽을 수 있는 처리/제외/실패 사유 설명",
        ["rowAudit.mappingConfidence"] = "CSV name 식별 신뢰도. 중복 name이면 ambiguousDuplicateSourceName",
        ["rowAudit.rawLine"] = "원본 CSV 데이터 행 문자열",
        ["rowAudit.rawFields"] = "원본 CSV 컬럼명-값 사전. CSV 내용 누락 방지를 위한 원문 필드 기록",
    };

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static InputAuditDocument Build(CsvInputPaths paths, RawDesignData data,
        IReadOnlyList<Cmb.Core.Model.Context.DroppedEquipmentMass>? droppedEquipment = null)
    {
        var duplicateNames = ReadDuplicateSourceNames(paths);
        var skips = data.Skips
            .GroupBy(s => (s.Kind, s.LineNumber, s.Name))
            .ToDictionary(g => g.Key, g => g.First());

        var doc = new InputAuditDocument();
        AddFileRows(doc, "Structure", paths.StructurePath, skips, duplicateNames);
        AddFileRows(doc, "Pipe", paths.PipePath, skips, duplicateNames);
        AddFileRows(doc, "Equipment", paths.EquipmentPath, skips, duplicateNames);
        MarkDroppedEquipment(doc, droppedEquipment);
        PopulateSummary(doc);
        doc.Summary.CoordinateScaleWarning = BuildScaleWarning(data);
        return doc;
    }

    // F-8(2): 모델 전체 좌표 범위가 mm 치고 지나치게 작으면(예: meter 로 입력) 경고 문자열을 만든다.
    // 범위가 정상(또는 좌표 없음)이면 null 을 반환해 경고 필드를 생략한다.
    private const double CoordinateScaleMinExtentMm = 100.0;

    private static string? BuildScaleWarning(RawDesignData data)
    {
        double extent = ComputeMaxCoordinateExtent(data);
        if (extent <= 0.0 || extent >= CoordinateScaleMinExtentMm) return null;
        return $"모델 좌표 전체 범위가 {extent:F3} 단위로 지나치게 작습니다. mm 가 아니라 meter 로 입력되었을 수 있습니다 " +
               $"(구조 모델은 보통 수천 mm 이상). 입력 단위를 확인하세요.";
    }

    private static double ComputeMaxCoordinateExtent(RawDesignData data)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        bool any = false;

        void Acc(double[]? p)
        {
            if (p is null || p.Length < 3) return;
            any = true;
            if (p[0] < minX) minX = p[0]; if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1]; if (p[1] > maxY) maxY = p[1];
            if (p[2] < minZ) minZ = p[2]; if (p[2] > maxZ) maxZ = p[2];
        }

        foreach (var b in data.Beams) { Acc(b.StartPos); Acc(b.EndPos); }
        foreach (var p in data.Pipes) { Acc(p.Pos); Acc(p.APos); Acc(p.LPos); Acc(p.P3Pos); }
        foreach (var e in data.Equips) { Acc(e.Pos); Acc(e.Cog); }

        if (!any) return 0.0;
        return Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
    }

    private static HashSet<(string Kind, string Name)> ReadDuplicateSourceNames(CsvInputPaths paths)
    {
        var result = new HashSet<(string, string)>();
        foreach (var (kind, path) in EnumeratePaths(paths))
        {
            if (path is null || !File.Exists(path)) continue;
            var names = ReadDataRows(path).Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var name in names)
                result.Add((kind, name));
        }
        return result;
    }

    private static void AddFileRows(
        InputAuditDocument doc,
        string kind,
        string? path,
        Dictionary<(string Kind, int LineNumber, string Name), ParseSkip> skips,
        HashSet<(string Kind, string Name)> duplicateNames)
    {
        if (path is null || !File.Exists(path))
        {
            doc.InputFiles.Add(new InputAuditFile { Kind = kind, Path = path ?? "", Exists = false });
            return;
        }

        var data = ReadDataRows(path);
        doc.InputFiles.Add(new InputAuditFile
        {
            Kind = kind,
            Path = path,
            Exists = true,
            Header = data.Header,
            PhysicalLineCount = data.PhysicalLineCount,
            DataRowCount = data.Rows.Count,
            BlankDataRowCount = data.Rows.Count(r => string.IsNullOrWhiteSpace(r.RawLine)),
        });

        foreach (var row in data.Rows)
        {
            skips.TryGetValue((kind, row.DataRowNumber, row.Name), out var skip);
            var (status, reasonCode, reason) = Classify(kind, row, skip);

            doc.RowAudit.Add(new InputAuditRow
            {
                Kind = kind,
                File = path,
                PhysicalLineNumber = row.PhysicalLineNumber,
                DataRowNumber = row.DataRowNumber,
                Name = row.Name,
                Status = status,
                ReasonCode = reasonCode,
                Reason = reason,
                MappingConfidence = duplicateNames.Contains((kind, row.Name))
                    ? "ambiguousDuplicateSourceName"
                    : "sourceName",
                RawLine = row.RawLine,
                RawFields = row.Fields,
            });
        }
    }

    private static (string Status, string ReasonCode, string Reason) Classify(
        string kind,
        CsvAuditRawRow row,
        ParseSkip? skip)
    {
        if (string.IsNullOrWhiteSpace(row.RawLine))
            return ("blank", "blank_line", "Blank CSV data line; parser skips blank lines.");

        if (skip is not null)
            return ("parseFailed", "parse_failed", skip.Reason);

        return kind switch
        {
            "Structure" => ClassifyIgnoredStructure(row),
            "Pipe" => ClassifyIgnoredPipe(row),
            "Equipment" => ClassifyIgnoredEquipment(row),
            _ => ("converted", "csv_row_accepted", "CSV row parsed successfully and was accepted for conversion."),
        };
    }

    internal static (string, string, string) ClassifyIgnoredStructure(CsvAuditRawRow row)
    {
        var start = ParseDoubles(Field(row, "poss"));
        var end = ParseDoubles(Field(row, "pose"));
        if (start.Length >= 3 && end.Length >= 3 && SamePoint(start, end))
            return ("ignored", "zero_length_structure", "Structure start and end coordinates are identical, so it has no convertible length.");

        var size = Field(row, "size");
        var section = size.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!IsSupportedStructureSection(section))
            return ("ignored", "unsupported_structure_section", $"Structure section '{section}' is not mapped to a supported beam section kind.");

        // F-8(1): 파싱된 단면 치수(size 의 '_' 뒤 숫자들)에 0 이하가 섞이면 유효 단면을 만들 수 없다.
        // 배관 outDia<=0(pipe_outdia_zero) 과 동일 계열의 형상 오류로 감사한다(기존엔 조용히 통과).
        int usIdx = size.IndexOf('_');
        if (usIdx >= 0 && usIdx + 1 < size.Length)
        {
            var dims = ParseDoubles(size[(usIdx + 1)..]);
            if (dims.Length > 0 && dims.Any(d => d <= 0))
                return ("error", "structure_dim_nonpositive",
                    $"Structure size '{size}' has a non-positive dimension; a valid beam section cannot be built so the geometry is dropped during model conversion.");
        }

        return ("converted", "csv_row_accepted", "Structure row parsed successfully and was accepted for conversion.");
    }

    // 직선·곡선 배관 부재 타입 — outDia 로 빔 단면(PBEAML Tube)을 만들어야 하는 행.
    // FeModelBuilder.IsLinePipeType 과 동일 집합. 이들에 outDia<=0 이면 형상을 만들 수 없어
    // 모델 변환 단계에서 통째로 누락되므로 입력 데이터 오류로 본다.
    private static readonly HashSet<string> LinePipeGeometryTypes = new(StringComparer.Ordinal)
    {
        "TUBI", "ELBO", "BEND", "OLET", "REDU", "COUP", "TEE",
    };

    internal static (string, string, string) ClassifyIgnoredPipe(CsvAuditRawRow row)
    {
        var type = Field(row, "type").Trim().ToUpperInvariant();
        // UBOLT는 외경·길이·질량 없이도 FeModelBuilder에서 RBE2로 변환된다.
        if (type == "UBOLT")
            return ("converted", "ubolt_rbe", "UBOLT row is converted to an RBE2 connection; beam geometry and mass are not required.");

        var mass = ParseDouble(Field(row, "mass"));
        var outDia = ParseDouble(Field(row, "outDia"));
        var thick = ParseDouble(Field(row, "thick"));
        var aPos = ParseDoubles(Field(row, "apos"));
        var lPos = ParseDoubles(Field(row, "lpos"));

        var isLinePipe = LinePipeGeometryTypes.Contains(type);
        var zeroLength = aPos.Length >= 3 && lPos.Length >= 3 && SamePoint(aPos, lPos);

        if (type == "ATTA" && mass <= 0)
            return ("ignored", "zero_mass_attachment", "ATTA row has mass <= 0, so it is excluded from CSV conversion output.");

        // 데이터 오류: 배관 부재(TUBI/ELBO/BEND/…)인데 외경(outDia)이 0 이하.
        // 빔 단면을 만들 수 없어 모델 변환 시 이 행의 형상이 그대로 누락된다(조용히 드롭됨).
        // (실제 사례: ELBO 행의 직경이 형제 TUBI 행에만 기록되어 ELBO outDia 가 0 → 곡선부 누락)
        if (isLinePipe && outDia <= 0)
            return ("error", "pipe_outdia_zero",
                $"Pipe '{type}' row has outDia <= 0; its beam section cannot be built so the geometry is dropped during model conversion. Provide a positive outDia (for elbows the diameter is often only on the sibling TUBI row).");

        // 데이터 오류: 관 두께(thick)가 외경(outDia)의 절반 이상 → PBEAML TUBE 내부 반경(=outDia/2 − thick)이
        // 0 이하(음수)가 되어 형상이 어긋난 채 조용히 export 된다. outDia<=0 과 같은 계열의 형상 오류로 본다.
        if (isLinePipe && outDia > 0 && thick > 0 && thick >= outDia / 2.0)
            return ("error", "pipe_wall_too_thick",
                $"Pipe '{type}' row has wall thickness {thick} >= outDia/2 ({outDia / 2.0}); the PBEAML TUBE inner radius (outDia/2 - thick) would be <= 0, producing a malformed section. Provide thick < outDia/2.");

        // 길이 0 배관 부재(TUBI/ELBO/TEE/…): 양 끝점(apos==lpos)이 동일해 빔 형상을 만들 수 없다.
        // 이들 line-pipe 타입은 FeModelBuilder 에서 point mass(CONM2)로 변환되지 않으므로 mass 컬럼은 무관하다.
        // → 제외 사유를 'mass'(오해 유발)가 아니라 '길이 0(형상 없음)' 으로 명확히 보고한다.
        //   (TEE 가 mass=0 이라서 빠지는 것으로 사용자가 오해하던 문제 교정)
        if (isLinePipe && zeroLength)
            return ("ignored", "zero_length_pipe",
                $"Pipe '{type}' has identical endpoints (zero length), so no beam geometry can be built. Line-pipe fittings are not converted to a point mass, so the mass value is irrelevant here.");

        if (outDia <= 0 && mass <= 0)
            return ("ignored", "no_geometry_and_zero_mass", "Pipe row has outDia <= 0 and mass <= 0, so it has neither geometry nor mass to convert.");

        // 비 line-pipe(FLAN/VALV 등)의 길이 0: 질량이 있으면 point mass 로 수용되므로,
        // 질량까지 0 일 때에만 제외한다. 여기서는 mass 가 실제 제외 사유의 일부이므로 함께 명시한다.
        if (zeroLength && mass <= 0)
            return ("ignored", "zero_length_pipe_without_mass",
                "Pipe has identical endpoints (zero length) and mass <= 0, so it has neither beam geometry nor a point mass to convert.");

        return ("converted", "csv_row_accepted", "Pipe row parsed successfully and was accepted for conversion.");
    }

    private static (string, string, string) ClassifyIgnoredEquipment(CsvAuditRawRow row)
    {
        var mass = ParseDouble(Field(row, "mass"));
        if (mass <= 0)
            return ("ignored", "zero_mass_equipment", "Equipment mass <= 0, so it is excluded from CSV conversion output.");
        return ("converted", "csv_row_accepted", "Equipment row parsed successfully and was accepted for conversion.");
    }

    // 장비 질량 누락 → 그 CSV 행을 error / equipment_mass_not_attached 로. 장비 행엔 줄 번호가 없어서, 엔진이 CSV 순서대로
    // 읽는 것을 이용해 같은 name 의 아직 표시하지 않은 첫 converted 행에 붙인다.
    private static void MarkDroppedEquipment(InputAuditDocument doc,
        IReadOnlyList<Cmb.Core.Model.Context.DroppedEquipmentMass>? dropped)
    {
        if (dropped is null || dropped.Count == 0) return;
        var marked = new HashSet<InputAuditRow>();
        foreach (var d in dropped)
        {
            var row = doc.RowAudit.FirstOrDefault(r => r.Kind == "Equipment" && r.Status == "converted"
                && r.Name.Trim() == d.Name.Trim() && !marked.Contains(r));
            if (row is null) continue;
            marked.Add(row);
            row.Status = "error";
            row.ReasonCode = "equipment_mass_not_attached";
            row.Reason = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"장비 질량 {d.MassKg:0.#}kg 이 모델에서 빠졌습니다: {d.Reason}. 장비 CSV 의 cog/interPos 를 확인하세요.");
        }
    }

    private static void PopulateSummary(InputAuditDocument doc)
    {
        doc.Summary.TotalDataRows = doc.RowAudit.Count;
        doc.Summary.ConvertedRows = doc.RowAudit.Count(r => r.Status == "converted");
        doc.Summary.IgnoredRows = doc.RowAudit.Count(r => r.Status == "ignored");
        doc.Summary.ErrorRows = doc.RowAudit.Count(r => r.Status == "error");
        doc.Summary.ParseFailedRows = doc.RowAudit.Count(r => r.Status == "parseFailed");
        doc.Summary.BlankRows = doc.RowAudit.Count(r => r.Status == "blank");
        doc.Summary.AmbiguousDuplicateSourceNameRows = doc.RowAudit.Count(r => r.MappingConfidence == "ambiguousDuplicateSourceName");
        // 문제 행(오류/제외/실패/공백)을 사유 코드별로 집계 — UI 의 사유 breakdown 에 사용.
        doc.Summary.IgnoredByReason = doc.RowAudit
            .Where(r => r.Status is "error" or "ignored" or "blank" or "parseFailed")
            .GroupBy(r => r.ReasonCode)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static (List<string> Header, List<CsvAuditRawRow> Rows, int PhysicalLineCount) ReadDataRows(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines.Length > 0
            ? SplitCsvLine(lines[0]).Select(h => h.Trim()).ToList()
            : [];
        var rows = new List<CsvAuditRawRow>();
        int dataRow = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            dataRow++;
            var raw = lines[i];
            var values = SplitCsvLine(raw);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < header.Count; c++)
                fields[header[c]] = c < values.Length ? values[c].Trim() : "";

            rows.Add(new CsvAuditRawRow(
                PhysicalLineNumber: i + 1,
                DataRowNumber: dataRow,
                Name: fields.GetValueOrDefault("name", ""),
                RawLine: raw,
                Fields: fields));
        }
        return (header, rows, lines.Length);
    }

    private static IEnumerable<(string Kind, string? Path)> EnumeratePaths(CsvInputPaths paths)
    {
        yield return ("Structure", paths.StructurePath);
        yield return ("Pipe", paths.PipePath);
        yield return ("Equipment", paths.EquipmentPath);
    }

    private static string[] SplitCsvLine(string line) => line.Split(',');

    private static string Field(CsvAuditRawRow row, string name) =>
        row.Fields.GetValueOrDefault(name, "");

    private static bool IsSupportedStructureSection(string section) =>
        section.ToUpperInvariant() is "ANG" or "BEAM" or "BSC" or "BULB" or "FBAR" or "RBAR" or "TUBE";

    private static bool SamePoint(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) < 1e-9 &&
        Math.Abs(a[1] - b[1]) < 1e-9 &&
        Math.Abs(a[2] - b[2]) < 1e-9;

    private static double[] ParseDoubles(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?");
        return matches.Select(m => ParseDouble(m.Value)).ToArray();
    }

    private static double ParseDouble(string text) =>
        double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0.0;
}

internal sealed record CsvAuditRawRow(
    int PhysicalLineNumber,
    int DataRowNumber,
    string Name,
    string RawLine,
    Dictionary<string, string> Fields);
