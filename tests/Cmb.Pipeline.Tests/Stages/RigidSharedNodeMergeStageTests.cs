using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class RigidSharedNodeMergeStageTests
{
    private static StageContext MakeCtx(FeModel model)
        => new(model, new RunOptions(new Tolerances()), NullLogger.Instance);

    private static Node N(int id, double x = 0, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static RigidElement Rbe(int id, int indep, IEnumerable<int> deps, string remark = "")
        => new(id, indep, deps, remark);

    // 한 노드가 등장하는 RBE2 개수
    private static int RbeCountFor(FeModel m, int nodeId)
        => m.Rigids.Count(r => r.IndependentNodeId == nodeId || r.DependentNodeIds.Contains(nodeId));

    [Fact]
    public void NoRigids_ReturnsTrue()
    {
        var m = new FeModel();
        new RigidSharedNodeMergeStage().Execute(MakeCtx(m)).Should().BeTrue();
        m.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void DisjointRigids_NoSharedNode_NotMerged()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3), N(4)]);
        m.Rigids.Add(Rbe(10, 1, [2]));
        m.Rigids.Add(Rbe(11, 3, [4]));

        new RigidSharedNodeMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().HaveCount(2);
    }

    // 회귀: struData_edit.bdf 의 Node 275 케이스 —
    // RBE A(indep 1093 → dep 275) + RBE B(indep 275 → dep 161). 275 가 한쪽 dep, 한쪽 indep.
    [Fact]
    public void CascadeSharedNode_DepInOne_IndepInOther_MergedToSingleRbe()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1093), N(275), N(161)]);
        m.Rigids.Add(Rbe(10, 1093, [275]));   // 275 는 dep
        m.Rigids.Add(Rbe(11, 275, [161]));    // 275 는 indep

        new RigidSharedNodeMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle("두 RBE2 는 노드 275 를 공유하므로 하나로 병합되어야 한다");
        var merged = m.Rigids[0];
        var allNodes = new[] { merged.IndependentNodeId }
            .Concat(merged.DependentNodeIds).OrderBy(x => x).ToList();
        allNodes.Should().BeEquivalentTo([161, 275, 1093]);

        // 핵심 불변식: 어떤 노드도 2개 이상의 RBE2 에 등장하지 않는다
        RbeCountFor(m, 275).Should().Be(1);
        RbeCountFor(m, 1093).Should().Be(1);
        RbeCountFor(m, 161).Should().Be(1);
    }

    [Fact]
    public void SharedDependentNode_TwoIndeps_Merged()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(9)]);
        m.Rigids.Add(Rbe(10, 1, [9]));   // dep 9
        m.Rigids.Add(Rbe(11, 2, [9]));   // dep 9 (공유)

        new RigidSharedNodeMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle();
        RbeCountFor(m, 9).Should().Be(1);
    }

    [Fact]
    public void ThreeRigids_ChainedBySharedNodes_AllMerged()
    {
        // 10: 1→2, 11: 2→3, 12: 3→4  (2,3 으로 체인)
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3), N(4)]);
        m.Rigids.Add(Rbe(10, 1, [2]));
        m.Rigids.Add(Rbe(11, 2, [3]));
        m.Rigids.Add(Rbe(12, 3, [4]));

        new RigidSharedNodeMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle();
        var merged = m.Rigids[0];
        var allNodes = new[] { merged.IndependentNodeId }
            .Concat(merged.DependentNodeIds).OrderBy(x => x).ToList();
        allNodes.Should().BeEquivalentTo([1, 2, 3, 4]);
        foreach (int nid in new[] { 1, 2, 3, 4 })
            RbeCountFor(m, nid).Should().Be(1);
    }

    [Fact]
    public void UboltRemark_Preserved_WhenAnyClusterMemberIsUbolt()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3)]);
        m.Rigids.Add(Rbe(10, 1, [2], remark: ""));
        m.Rigids.Add(Rbe(11, 2, [3], remark: "UBOLT"));

        new RigidSharedNodeMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle();
        m.Rigids[0].Remark.Should().Be("UBOLT");
    }

    [Fact]
    public void EmitsInfoDiagnostic_WhenMerged()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3)]);
        m.Rigids.Add(Rbe(10, 1, [2]));
        m.Rigids.Add(Rbe(11, 2, [3]));

        var ctx = MakeCtx(m);
        new RigidSharedNodeMergeStage().Execute(ctx);

        ctx.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Info && d.Code == "RIGID_SHARED_NODE_MERGED");
    }
}
