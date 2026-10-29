// M-1 리팩터: Program.cs 에서 분리한 CSV 로드/산출물 파일명/출력 폴더 헬퍼. 동작 불변.
using Cmb.Core.Model.Raw;
using Cmb.Io.Csv;

internal static class CsvInput
{
    internal static (RawDesignData? data, string baseName, string csvDir, CsvInputPaths paths, string? error) LoadCsv(
        DirectoryInfo? input, FileInfo? stru, FileInfo? pipe, FileInfo? equip)
    {
        var loader = new CsvDesignLoader();
    
        if (stru is not null || pipe is not null || equip is not null)
        {
            var firstFile = (stru ?? pipe ?? equip)!;
            var baseName  = firstFile.Name.Replace(".csv", "", StringComparison.OrdinalIgnoreCase);
            var csvDir    = firstFile.DirectoryName ?? ".";
            var paths = new CsvInputPaths(stru?.FullName, pipe?.FullName, equip?.FullName);
            return (loader.Load(paths.StructurePath, paths.PipePath, paths.EquipmentPath), baseName, csvDir, paths, null);
        }
    
        if (input is not null)
        {
            if (!input.Exists)
                return (null, "", "", new CsvInputPaths(null, null, null), $"[오류] 입력 폴더를 찾을 수 없습니다: {input.FullName}");
    
            var files = Directory.GetFiles(input.FullName, "*.csv");
            var paths = new CsvInputPaths(
                MatchFirst(files, "struData", "structure", "support", "supt"),
                MatchFirst(files, "pipeData", "pipe"),
                MatchFirst(files, "equpData", "equip"));
            return (loader.Load(paths.StructurePath, paths.PipePath, paths.EquipmentPath), input.Name, input.FullName, paths, null);
        }
    
        return (null, "", "", new CsvInputPaths(null, null, null), "[오류] --input 폴더 또는 --stru/--pipe/--equip 파일 중 하나 이상을 지정하세요.");
    }
    
    internal static string? MatchFirst(string[] files, params string[] patterns)
    {
        foreach (var pat in patterns)
        {
            var hit = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(pat, StringComparison.OrdinalIgnoreCase) ||
                f.Contains(pat, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
    
    // ── 최종 산출물 파일명 — CSV 파일명에서 struData/pipeData/equpData 토큰 제거 ──
    // 예) "3468-35030-struData-A453412_20260108.csv" → "3468-35030-A453412_20260108"
    internal static string DeriveDesignName(string fallback, CsvInputPaths paths)
    {
        string? sample = paths.StructurePath ?? paths.PipePath ?? paths.EquipmentPath;
        if (string.IsNullOrEmpty(sample)) return fallback;
    
        string name = Path.GetFileNameWithoutExtension(sample);
        foreach (var token in new[] { "struData", "pipeData", "equpData" })
            name = StripDataToken(name, token);
    
        name = name.Trim('-', '_', ' ');
        return string.IsNullOrEmpty(name) ? fallback : name;
    }
    
    
    internal static string StripDataToken(string name, string token)
    {
        int idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return name;
    
        int start = idx;
        int end   = idx + token.Length;
        // 인접 하이픈 1개 흡수: "-token-" → 한쪽만 제거하여 양 옆 식별자가 자연스럽게 이어지도록
        if (start > 0 && name[start - 1] == '-')      start--;
        else if (end < name.Length && name[end] == '-') end++;
    
        return name[..start] + name[end..];
    }
    
    // ── 타임스탬프 출력 폴더 생성 ─────────────────────────────────────────────────
    
    internal static string MakeOutputDir(string csvDir)
    {
        var ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(csvDir, ts);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
