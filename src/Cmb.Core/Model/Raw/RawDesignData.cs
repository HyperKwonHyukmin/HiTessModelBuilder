namespace Cmb.Core.Model.Raw;

public sealed record RawBeamRow(
    string Name,
    string SectionType,
    string SizeRaw,
    double[] Dims,
    double[] StartPos,
    double[] EndPos,
    double[] Ori,
    string Weld
);

public sealed record RawPipeRow(
    string Name,
    string Type,
    string Branch,
    double[] Pos,
    double[] APos,
    double[] LPos,
    double[] Normal,
    double[]? InterPos,
    double[]? P3Pos,
    string? Rest,
    double OutDia,
    double Thick,
    double OutDia2,
    double Thick2,
    double Mass,
    string? Remark
);

public sealed record RawEquipRow(
    string Name,
    double[] Pos,
    double[] Cog,
    double Mass
);

public sealed record ParseSkip(
    string Kind,
    int LineNumber,
    string Name,
    string Reason
);

public sealed record RawDesignData(
    IReadOnlyList<RawBeamRow> Beams,
    IReadOnlyList<RawPipeRow> Pipes,
    IReadOnlyList<RawEquipRow> Equips,
    IReadOnlyList<ParseSkip> Skips
);
