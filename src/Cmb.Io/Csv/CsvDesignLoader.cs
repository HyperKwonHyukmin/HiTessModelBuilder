using Cmb.Core.Model.Raw;

namespace Cmb.Io.Csv;

public sealed class CsvDesignLoader
{
    private readonly HiTessStructureCsvReader _struReader = new();
    private readonly HiTessPipeCsvReader      _pipeReader = new();
    private readonly HiTessEquipCsvReader     _equipReader = new();

    /// <summary>
    /// Discovers CSV files in <paramref name="folderPath"/> by name pattern
    /// ("struData", "pipeData", "equpData") and loads all three.
    /// </summary>
    public RawDesignData LoadFolder(string folderPath)
    {
        var files = Directory.GetFiles(folderPath, "*.csv");
        var struPath  = MatchFirst(files, "struData", "structure");
        var pipePath  = MatchFirst(files, "pipeData", "pipe");
        var equipPath = MatchFirst(files, "equpData", "equip");
        return Load(struPath, pipePath, equipPath);
    }

    private static string? MatchFirst(string[] files, params string[] patterns)
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

    /// <summary>
    /// Loads from explicit file paths. Any path may be null to skip that category.
    /// </summary>
    public RawDesignData Load(string? struPath, string? pipePath, string? equipPath)
    {
        var allSkips = new List<ParseSkip>();

        IReadOnlyList<RawBeamRow> beams = [];
        if (struPath is not null && File.Exists(struPath))
        {
            var (rows, skips) = _struReader.Read(struPath);
            beams = rows;
            allSkips.AddRange(skips);
        }

        IReadOnlyList<RawPipeRow> pipes = [];
        if (pipePath is not null && File.Exists(pipePath))
        {
            var (rows, skips) = _pipeReader.Read(pipePath);
            pipes = rows;
            allSkips.AddRange(skips);
        }

        IReadOnlyList<RawEquipRow> equips = [];
        if (equipPath is not null && File.Exists(equipPath))
        {
            var (rows, skips) = _equipReader.Read(equipPath);
            equips = rows;
            allSkips.AddRange(skips);
        }

        return new RawDesignData(beams, pipes, equips, allSkips);
    }
}
