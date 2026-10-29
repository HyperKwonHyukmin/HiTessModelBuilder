using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class RedundantBridgeRbeRemoveStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model)
        => new(model, new RunOptions(new Tolerances()), NullLogger.Instance);

    private static Node N(int id, double x = 0, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, int propId = 1)
        => new(id, start, end, propId, EntityCategory.Structure, Up);

    // ── 회귀: degree-1 진짜 자유단 브릿지는 전역 component 동일성만으로 제거하면 안 된다 ──
    //
    // 실제 결함(N725): 막대 하단 자유단(degree=1)이 자기 막대를 타고 구조를 멀리 돌아
    // 앵글 절점과 같은 CBEAM component 에 속한다는 이유로 FREE_END_RBE 브릿지가 삭제되어
    // 자유단이 재고립됐다. 막대 하단↔앵글의 국부 하중경로는 이 브릿지뿐이므로 제거는 오판이다.
    [Fact]
    public void DegreeOneFreeEnd_SameComponent_FreeEndRbeBridgeKept()
    {
        var m = new FeModel();
        // I=1 (앵글 투영 타겟), 앵글선 1–2
        // 자유단 D=3 → 4 → 5 → 2 (막대가 구조를 멀리 돌아 같은 component 로 복귀)
        m.Nodes.AddRange([N(1), N(2), N(3), N(4), N(5)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4), E(12, 4, 5), E(13, 5, 2)]);
        // FreeEndRbe 가 만든 브릿지: indep=1(앵글), dep=3(자유단)
        m.Rigids.Add(new RigidElement(99, 1, [3], "FREE_END_RBE"));

        new RedundantBridgeRbeRemoveStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle(r =>
            r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(3),
            "degree-1 자유단의 브릿지는 유일한 국부 연결이므로 전역 component 동일성만으로 제거되면 안 된다");
    }

    // ── 보존(control): degree≥2 dep 의 자동 RBE 는 기존 dedup 동작을 유지한다 ──
    [Fact]
    public void DegreeTwoDep_SameComponent_StillRemoved()
    {
        var m = new FeModel();
        // dep=3 이 두 요소(11,12)에 걸려 degree=2 → 국부 대체 경로 존재 → 중복 제거 대상 유지
        m.Nodes.AddRange([N(1), N(2), N(3), N(4)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 2, 3), E(12, 3, 4), E(13, 4, 1)]);
        m.Rigids.Add(new RigidElement(99, 1, [3], "FINAL_MESH_PROXIMITY"));

        new RedundantBridgeRbeRemoveStage().Execute(MakeCtx(m));

        m.Rigids.Should().NotContain(r =>
            r.Remark == "FINAL_MESH_PROXIMITY" && r.DependentNodeIds.Contains(3));
    }

    // ── 보존(control): 다른 component 의 dep 는 (degree 무관) 절대 제거하지 않는다 ──
    [Fact]
    public void DifferentComponent_DepKept()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3), N(4)]);
        // 1–2 와 3–4 는 분리된 component. dep=3 은 indep=1 과 다른 component.
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);
        m.Rigids.Add(new RigidElement(99, 1, [3], "FREE_END_RBE"));

        new RedundantBridgeRbeRemoveStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle(r =>
            r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(3));
    }
}
