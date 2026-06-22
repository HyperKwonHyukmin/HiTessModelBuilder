using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;

namespace Cmb.Pipeline.Analysis;

/// <summary>
/// FeModel 전체 질량과 무게중심 계산. 단위계 [mm, tonne, s, N].
/// - BeamElement: 길이 × 단면적 × 재료 밀도 → 균등분포 → 중심은 양 끝 노드 중점
/// - PointMass: 노드 위치에 mass 그대로 누적
/// </summary>
public static class MassPropertiesAnalyzer
{
    public static MassProperties Analyze(FeModel model)
    {
        var nodeById     = model.Nodes.ToDictionary(n => n.Id);
        var sectionById  = model.Sections.ToDictionary(s => s.Id);
        var materialById = model.Materials.ToDictionary(m => m.Id);

        double total = 0, beamMass = 0, pmMass = 0;
        double mx = 0, my = 0, mz = 0;

        foreach (var e in model.Elements)
        {
            if (!nodeById.TryGetValue(e.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(e.EndNodeId,   out var nB)) continue;
            if (!sectionById.TryGetValue(e.PropertyId, out var sec)) continue;
            if (!materialById.TryGetValue(sec.MaterialId, out var mat)) continue;

            double length = nA.Position.DistanceTo(nB.Position);
            double area   = ComputeArea(sec);
            double mass   = length * area * mat.Rho;
            if (!(mass > 0)) continue;

            double cx = (nA.Position.X + nB.Position.X) * 0.5;
            double cy = (nA.Position.Y + nB.Position.Y) * 0.5;
            double cz = (nA.Position.Z + nB.Position.Z) * 0.5;

            total    += mass;
            beamMass += mass;
            mx += mass * cx;
            my += mass * cy;
            mz += mass * cz;
        }

        foreach (var pm in model.PointMasses)
        {
            if (pm.Mass <= 0) continue;
            if (!nodeById.TryGetValue(pm.NodeId, out var n)) continue;

            total  += pm.Mass;
            pmMass += pm.Mass;
            mx += pm.Mass * n.Position.X;
            my += pm.Mass * n.Position.Y;
            mz += pm.Mass * n.Position.Z;
        }

        var cg = total > 0
            ? new Point3(mx / total, my / total, mz / total)
            : new Point3(0, 0, 0);

        return new MassProperties(total, beamMass, pmMass, cg);
    }

    /// <summary>
    /// 정규화된 PBEAML dims에 대해 단면적 계산 (mm²).
    /// FeModelBuilder.NormalizeDims가 변환한 형태를 그대로 사용.
    /// </summary>
    private static double ComputeArea(BeamSection s)
    {
        var d = s.Dims;
        if (d is null || d.Length == 0) return 0.0;

        return s.Kind switch
        {
            // Stored [H-2TF, 2TF, BF, TW] → web(TW × (H-2TF)) + flanges(BF × 2TF)
            BeamSectionKind.H when d.Length >= 4
                => d[3] * d[0] + d[2] * d[1],

            // Stored [B, D, T, T] → leg1(B × T) + leg2((D-T) × T)
            BeamSectionKind.L when d.Length >= 4
                => d[0] * d[2] + (d[1] - d[2]) * d[3],

            // Stored [R_out, R_in] → π(R_out² - R_in²)
            BeamSectionKind.Tube when d.Length >= 2
                => Math.PI * (d[0] * d[0] - d[1] * d[1]),

            // Stored [R] → πR²
            BeamSectionKind.Rod when d.Length >= 1
                => Math.PI * d[0] * d[0],

            // PBEAML BAR [DIM1, DIM2] = width × height
            BeamSectionKind.Bar when d.Length >= 2
                => d[0] * d[1],

            // PBEAML CHAN [BF, H, TW, TF] → 2 flanges + web
            BeamSectionKind.Channel when d.Length >= 4
                => 2 * d[0] * d[3] + (d[1] - 2 * d[3]) * d[2],

            // PBEAML BOX [outerW, outerH, twTop/Bot, twSide] → outer - inner
            BeamSectionKind.Box when d.Length >= 4
                => d[0] * d[1] - Math.Max(0.0, (d[0] - 2 * d[3]) * (d[1] - 2 * d[2])),

            _ => d.Length >= 2 ? d[0] * d[1] : d[0] * d[0],
        };
    }
}

public sealed record MassProperties(
    double TotalMassTon,
    double BeamMassTon,
    double PointMassTon,
    Point3 CenterOfGravity);
