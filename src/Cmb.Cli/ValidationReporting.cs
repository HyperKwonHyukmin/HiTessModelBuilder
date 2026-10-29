// M-1 리팩터: Program.cs 에서 분리한 검증 결과/진단 집계 변환. 동작 불변.
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Core;

internal static class ValidationReporting
{
    internal static Diagnostic DiagnosticFromDto(DiagnosticDto dto)
    {
        var severity = dto.Severity.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };
        return new Diagnostic(severity, dto.Code, dto.Message, dto.ElemId, dto.NodeId);
    }
    
    internal static ValidationResultDto BuildValidationResult(
        IReadOnlyList<Diagnostic> stageDiagnostics, int groupCount, FeModel m)
    {
        int ec = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int wc = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int zeroCount       = stageDiagnostics.Count(d => d.Code == "ZERO_LENGTH_ELEMENT");
        int shortCount      = stageDiagnostics.Count(d => d.Code == "SHORT_ELEMENT");
        int emptyRbeCount   = stageDiagnostics.Count(d => d.Code == "EMPTY_RIGID");
        int idOverflowCount = stageDiagnostics.Count(d => d.Code == "ID_OVERFLOW");
        int orphanCount     = stageDiagnostics.Count(d => d.Code == "ORPHAN_NODE");
        int freeEndCount    = stageDiagnostics.Count(d => d.Code == "FREE_END_NODE");
        bool sg = groupCount <= 1;
        bool nz = zeroCount       == 0;
        bool ns = shortCount      == 0;
        bool no = orphanCount     == 0;
        bool nr = emptyRbeCount   == 0;
        bool ie = idOverflowCount == 0;
        int bc = m.Nodes.Count(n => n.HasTag(NodeTags.Boundary));
    
        // H-4: 연결성/경계조건 상태는 요약에 정보로 노출한다(다중 연결 그룹은 정상일 수 있고,
        // 경계조건 노드가 0 인 정상 모델도 존재하므로 Passed 는 이 값들로 뒤집지 않는다 — Passed 는 오류 수 기준 유지).
        string bcConn = $" [연결 그룹 {groupCount}개" + (groupCount > 1 ? "(다중 그룹은 정상일 수 있음)" : "") +
                        $", 경계조건 노드 {bc}개" + (bc == 0 ? "(SPC 확인 필요)" : "") + "]";
    
        string summary;
        if (ec > 0)
        {
            summary = $"모델에 오류 {ec}개가 있습니다 (경고 {wc}개).{bcConn}";
        }
        else
        {
            var parts = new List<string>();
            if (freeEndCount > 0) parts.Add($"자유단 {freeEndCount}");
            if (orphanCount  > 0) parts.Add($"고립노드 {orphanCount}");
            if (shortCount   > 0) parts.Add($"짧은요소 {shortCount}");
            string detail = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
            summary = $"모델이 유효합니다. 경고 {wc}개{detail}.{bcConn}";
        }
    
        return new ValidationResultDto
        {
            Passed = ec == 0, ErrorCount = ec, WarningCount = wc,
            Summary = summary,
            Checklist =
            [
                new() { Check = "connectivity",        Passed = sg,     Detail = sg ? "단일 연결 그룹"   : $"{groupCount}개 그룹 분리" },
                new() { Check = "noZeroLength",        Passed = nz,     Detail = nz ? "길이=0 요소 없음" : $"{zeroCount}개 길이=0 요소 (해석 불가)" },
                new() { Check = "noShortElements",     Passed = ns,     Detail = ns ? "짧은 요소 없음"   : $"{shortCount}개 짧은 요소 (메쉬 검토 권장)" },
                new() { Check = "noOrphanNodes",       Passed = no,     Detail = no ? "고립 노드 없음"   : $"{orphanCount}개 고립 노드" },
                new() { Check = "noEmptyRigids",       Passed = nr,     Detail = nr ? "유효 RBE2"        : $"{emptyRbeCount}개 빈 dep RBE2" },
                new() { Check = "bdfExportCompatible", Passed = ie,     Detail = ie ? "ID 8자리 이내"    : $"{idOverflowCount}개 ID 한계 초과" },
                new() { Check = "boundaryCondition",   Passed = bc > 0, Detail = bc > 0 ? $"경계조건 노드 {bc}개" : "경계조건 없음" },
            ],
        };
    }
    
    internal static DiagnosticCountsDto CountDiagnostics(IReadOnlyList<DiagnosticDto> diagnostics)
    {
        var counts = new DiagnosticCountsDto();
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity.ToLowerInvariant())
            {
                case "error": counts.Error++; break;
                case "warning": counts.Warning++; break;
                case "info": counts.Info++; break;
            }
            counts.ByCode[diagnostic.Code] = counts.ByCode.GetValueOrDefault(diagnostic.Code) + 1;
        }
        return counts;
    }
}
