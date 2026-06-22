using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// U-bolt 영역(pipe 단면 + axial 여유) exclusion zone 헬퍼.
/// BoxUboltConnection / FreeEndRbe 등 자유단·후보 검색 시 U-bolt 주변에
/// 잘못된 RBE 연결이 만들어지는 것을 막기 위해 공통으로 사용.
/// </summary>
internal readonly record struct UboltExclusionZone(
    Point3 Center,
    Vector3 Axis,
    double Radius,
    double HalfLength);

internal static class UboltExclusionZones
{
    /// <summary>
    /// 모든 U-bolt RBE2의 indep 노드를 중심으로 owner pipe 축 방향 ±(radius+axialSafetyMm),
    /// 반지름 = pipe 단면 최대 치수에 해당하는 원기둥 zone을 생성합니다.
    /// </summary>
    public static List<UboltExclusionZone> Build(
        FeModel model,
        IReadOnlyDictionary<int, Node> nodeById,
        IReadOnlyDictionary<int, BeamSection> sectionById,
        double axialSafetyMm)
    {
        var zones = new List<UboltExclusionZone>();

        var pipeOwnerByNid = new Dictionary<int, BeamElement>();
        foreach (var e in model.Elements)
        {
            if (e.Category != EntityCategory.Pipe) continue;
            pipeOwnerByNid.TryAdd(e.StartNodeId, e);
            pipeOwnerByNid.TryAdd(e.EndNodeId, e);
        }

        foreach (var rigid in model.Rigids)
        {
            if (rigid.Remark != "UBOLT") continue;
            if (!nodeById.TryGetValue(rigid.IndependentNodeId, out var center)) continue;
            if (!pipeOwnerByNid.TryGetValue(rigid.IndependentNodeId, out var ownerPipe) ||
                !nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) ||
                !nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
                continue;

            double radius = sectionById.TryGetValue(ownerPipe.PropertyId, out var sec)
                ? sec.MaxCrossSectionDim()
                : 0.0;
            if (radius < 1e-3) continue;

            var axis = pB.Position - pA.Position;
            if (axis.Length < 1e-9) continue;
            axis = axis.Normalize();

            zones.Add(new UboltExclusionZone(center.Position, axis, radius, radius + axialSafetyMm));
        }

        return zones;
    }

    /// <summary>점이 어느 zone 내부에 있는지 검사.</summary>
    public static bool IsInside(Point3 p, IReadOnlyList<UboltExclusionZone> zones)
    {
        foreach (var z in zones)
        {
            var diff  = p - z.Center;
            double along = Vector3.Dot(diff, z.Axis);
            if (Math.Abs(along) > z.HalfLength) continue;

            var perp = diff - z.Axis * along;
            if (perp.Length <= z.Radius) return true;
        }
        return false;
    }
}
