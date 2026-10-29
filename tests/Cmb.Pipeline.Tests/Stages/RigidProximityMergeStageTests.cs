using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class RigidProximityMergeStageTests
{
    private static StageContext MakeCtx(FeModel model, double tolMm = 50.0)
        => new(model, new RunOptions(new Tolerances(RigidProximityMergeMm: tolMm)),
            NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static RigidElement Rbe(int id, int indep, IEnumerable<int> deps,
        string remark = "")
        => new(id, indep, deps, remark);

    // ── 빈 / 단일 ─────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var m = new FeModel();
        var result = new RigidProximityMergeStage().Execute(MakeCtx(m));
        result.Should().BeTrue();
        m.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void SingleRigid_NoMerge()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));

        new RigidProximityMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle();
        m.Rigids[0].Id.Should().Be(10);
    }

    // ── 멀리 떨어진 두 RBE ────────────────────────────────────────────────────

    [Fact]
    public void TwoRigids_FarApart_NoMerge()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 50), N(3, 200, 0), N(4, 200, 50)]);
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));
        m.Rigids.Add(Rbe(11, 3, [4], "UBOLT"));

        new RigidProximityMergeStage().Execute(MakeCtx(m, tolMm: 50.0));

        m.Rigids.Should().HaveCount(2);
    }

    // ── 동일 노드 공유 ────────────────────────────────────────────────────────

    [Fact]
    public void TwoRigids_SharedIndependentNode_Merged()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(3, 0, 200)]);
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));
        m.Rigids.Add(Rbe(11, 1, [3], "UBOLT")); // shared indep N1

        new RigidProximityMergeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle();
        var merged = m.Rigids[0];
        merged.IndependentNodeId.Should().Be(1);
        merged.DependentNodeIds.Should().BeEquivalentTo([2, 3]);
    }

    // ── 50mm 이내 GM 노드 ─────────────────────────────────────────────────────

    [Fact]
    public void TwoRigids_NodesWithin50mm_Merged_LargerGmIsMaster()
    {
        // R10: indep=1, deps=[2,3]   GM count = 2
        // R11: indep=4, deps=[5]     GM count = 1
        // N3 ↔ N5 거리 30mm → 같은 클러스터
        var m = new FeModel();
        m.Nodes.AddRange([
            N(1, 0, 0),    N(2, 10, 0),  N(3, 100, 0),
            N(4, 200, 0),  N(5, 100, 30)]);
        m.Rigids.Add(Rbe(10, 1, [2, 3], "UBOLT"));
        m.Rigids.Add(Rbe(11, 4, [5], "UBOLT"));

        new RigidProximityMergeStage().Execute(MakeCtx(m, tolMm: 50.0));

        m.Rigids.Should().ContainSingle();
        var merged = m.Rigids[0];
        merged.Id.Should().Be(10);              // R10이 master (GM 더 많음)
        merged.IndependentNodeId.Should().Be(1);
        merged.DependentNodeIds.Should().BeEquivalentTo([2, 3, 4, 5]);
    }

    // ── Transitive 클러스터 ───────────────────────────────────────────────────

    [Fact]
    public void ThreeRigids_TransitiveCluster_AllMerged()
    {
        // A↔B 30mm, B↔C 30mm, A↔C 70mm → A,B,C 모두 한 클러스터
        var m = new FeModel();
        m.Nodes.AddRange([
            N(1, 0,   0), N(2, 0,   100),    // A: indep=1, dep=2
            N(3, 30,  0), N(4, 30,  100),    // B: indep=3, dep=4 (N3 - N1 거리 30mm)
            N(5, 60,  0), N(6, 60,  100)]);  // C: indep=5, dep=6 (N5 - N3 거리 30mm, N5 - N1 거리 60mm > 50)
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));
        m.Rigids.Add(Rbe(11, 3, [4], "UBOLT"));
        m.Rigids.Add(Rbe(12, 5, [6], "UBOLT"));

        new RigidProximityMergeStage().Execute(MakeCtx(m, tolMm: 50.0));

        m.Rigids.Should().ContainSingle();
        var merged = m.Rigids[0];
        var allNodes = new[] { merged.IndependentNodeId }
            .Concat(merged.DependentNodeIds).OrderBy(x => x).ToList();
        allNodes.Should().BeEquivalentTo([1, 2, 3, 4, 5, 6]);
    }

    // ── UBOLT remark 보존 ────────────────────────────────────────────────────

    [Fact]
    public void UboltRemark_Preserved_WhenAnyClusterMemberIsUbolt()
    {
        // R10: UBOLT remark, R11: 일반 RBE — 클러스터 결과는 "UBOLT"
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(3, 30, 0), N(4, 30, 100)]);
        m.Rigids.Add(Rbe(10, 1, [2], remark: ""));        // 일반
        m.Rigids.Add(Rbe(11, 3, [4], remark: "UBOLT"));   // UBOLT

        new RigidProximityMergeStage().Execute(MakeCtx(m, tolMm: 50.0));

        m.Rigids.Should().ContainSingle();
        m.Rigids[0].Remark.Should().Be("UBOLT");
    }

    // ── Tiebreak: GM 개수 동일 시 indep ID 최소 ───────────────────────────────

    [Fact]
    public void TieBreak_LowestIndepIdWins_WhenSameGmCount()
    {
        // R20과 R10 모두 GM 1개, indep id는 5와 1 → 1이 master
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(5, 30, 0), N(6, 30, 100)]);
        m.Rigids.Add(Rbe(20, 5, [6], "UBOLT"));   // 먼저 추가 (id=20, indep=5)
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));   // 후 추가 (id=10, indep=1)

        new RigidProximityMergeStage().Execute(MakeCtx(m, tolMm: 50.0));

        m.Rigids.Should().ContainSingle();
        var merged = m.Rigids[0];
        merged.IndependentNodeId.Should().Be(1);  // tiebreak: indep id 최소
        merged.Id.Should().Be(10);                // master id = R10
    }

    // ── 진단 메시지 ───────────────────────────────────────────────────────────

    [Fact]
    public void EmitsInfoDiagnostic_WhenClustersMerged()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(3, 30, 0), N(4, 30, 100)]);
        m.Rigids.Add(Rbe(10, 1, [2], "UBOLT"));
        m.Rigids.Add(Rbe(11, 3, [4], "UBOLT"));

        var ctx = MakeCtx(m, tolMm: 50.0);
        new RigidProximityMergeStage().Execute(ctx);

        ctx.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Info && d.Code == "RIGID_PROXIMITY_MERGED");
    }

    // ── F-3: 서로 다른 소스(다른 배관)의 U-bolt 가 거리 기반으로 병합되면 감사 로그를 남긴다 ──

    [Fact]
    public void MergingDistinctSourceMembers_EmitsAuditListingBothSources()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(3, 30, 0), N(4, 30, 100)]);
        m.Rigids.Add(new RigidElement(10, 1, [2], "UBOLT", "PIPE_A"));
        m.Rigids.Add(new RigidElement(11, 3, [4], "UBOLT", "PIPE_B"));

        var ctx = MakeCtx(m, tolMm: 50.0);
        new RigidProximityMergeStage().Execute(ctx);

        m.Rigids.Should().ContainSingle();
        ctx.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Info &&
            d.Code == "RIGID_PROXIMITY_MERGE_AUDIT" &&
            d.Message.Contains("PIPE_A") && d.Message.Contains("PIPE_B"));
    }

    [Fact]
    public void MergingSameSourceMembers_DoesNotEmitDistinctPairAudit()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 0, 100), N(3, 30, 0), N(4, 30, 100)]);
        m.Rigids.Add(new RigidElement(10, 1, [2], "UBOLT", "PIPE_A"));
        m.Rigids.Add(new RigidElement(11, 3, [4], "UBOLT", "PIPE_A")); // 동일 소스

        var ctx = MakeCtx(m, tolMm: 50.0);
        new RigidProximityMergeStage().Execute(ctx);

        m.Rigids.Should().ContainSingle();
        ctx.Diagnostics.Should().NotContain(d => d.Code == "RIGID_PROXIMITY_MERGE_AUDIT");
    }
}
