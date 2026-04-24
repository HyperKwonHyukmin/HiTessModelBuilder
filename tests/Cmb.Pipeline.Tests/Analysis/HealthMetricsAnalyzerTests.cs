using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Analysis;

public class HealthMetricsAnalyzerTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;
    private static readonly Tolerances DefaultTol = new();

    private static Node N(int id, double x = 0, double y = 0, double z = 0, NodeTags tags = NodeTags.None)
        => new(id, new Point3(x, y, z), tags);

    private static BeamElement E(int id, int start, int end, EntityCategory cat = EntityCategory.Structure)
        => new(id, start, end, 1, cat, Up);

    private static RigidElement R(int id, int indep, IEnumerable<int> deps, string remark = "")
        => new(id, indep, [..deps], remark);

    // ── 빈 모델 ───────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyModel_AllZero()
    {
        var result = HealthMetricsAnalyzer.Analyze(new FeModel(), null, DefaultTol);

        result.Totals.NodeCount.Should().Be(0);
        result.Totals.ElementCount.Should().Be(0);
        result.Issues.FreeEndNodes.Should().Be(0);
        result.Issues.OrphanNodes.Should().Be(0);
        result.Issues.ShortElements.Should().Be(0);
        result.Issues.UnresolvedUbolts.Should().Be(0);
        result.Issues.DisconnectedGroups.Should().Be(0);
        result.DiagnosticCounts.Error.Should().Be(0);
    }

    // ── FreeEndNodes ─────────────────────────────────────────────────────────

    [Fact]
    public void FreeEndNodes_DegreeOne_NotBoundary_Counted()
    {
        // 체인 N1-N2: 노드 1·2 모두 degree=1
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.FreeEndNodes.Should().Be(2);
    }

    [Fact]
    public void FreeEndNodes_BoundaryTag_NotCounted()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0, 0, NodeTags.Boundary), N(2, 100, 0, 0, NodeTags.Boundary)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.FreeEndNodes.Should().Be(0);
    }

    [Fact]
    public void FreeEndNodes_RbeConnectionCountsAsDegree()
    {
        // N1-N2 체인. N1은 RBE의 dependent → degree 증가 → 자유단 아님
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 50, 50)]);
        model.Elements.Add(E(10, 1, 2));
        // N3(independent) → N1(dependent)
        model.Rigids.Add(R(20, 3, [1], "UBOLT"));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        // N1: degree 1(elem)+1(RBE)=2, N2: degree 1 → N2가 자유단
        result.Issues.FreeEndNodes.Should().Be(1);
    }

    // ── OrphanNodes ──────────────────────────────────────────────────────────

    [Fact]
    public void OrphanNode_Counted()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1), N(2), N(3, 999, 999)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.OrphanNodes.Should().Be(1);
    }

    // ── ShortElements ────────────────────────────────────────────────────────

    [Fact]
    public void ShortElement_BelowTol_Counted()
    {
        // ShortElemMinMm=5, 길이 3mm
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 3)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.ShortElements.Should().Be(1);
    }

    [Fact]
    public void NormalElement_AboveTol_NotCounted()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.ShortElements.Should().Be(0);
    }

    // ── UnresolvedUbolts ─────────────────────────────────────────────────────

    [Fact]
    public void UnresolvedUbolt_FallbackBoundary_Counted()
    {
        var model = new FeModel();
        // N1: structure (independent), N2: pipe (dependent, Boundary tag = fallback)
        model.Nodes.AddRange([N(1, 0), N(2, 100, 0, 0, NodeTags.Boundary)]);
        model.Elements.AddRange([E(10, 1, 1), E(11, 2, 2)]); // dummy elems to avoid orphan noise
        model.Rigids.Add(R(20, 1, [2], "UBOLT"));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.UnresolvedUbolts.Should().Be(1);
    }

    [Fact]
    public void ResolvedUbolt_NoBoundaryDependent_NotCounted()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(R(20, 1, [2], "UBOLT"));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.UnresolvedUbolts.Should().Be(0);
    }

    // ── DisconnectedGroups ───────────────────────────────────────────────────

    [Fact]
    public void DisconnectedGroups_EqualsConnectivityGroupCountMinusOne()
    {
        // 분리된 체인 2개 → groupCount=2 → disconnectedGroups=1
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 500), N(4, 600)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Issues.DisconnectedGroups.Should().Be(1);
    }

    // ── Totals ───────────────────────────────────────────────────────────────

    [Fact]
    public void Totals_NodeElementRigidPointMass_Counts()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 200), N(4, 300)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);
        model.Rigids.Add(R(20, 1, [2]));
        model.PointMasses.Add(new PointMass(30, 1, 100.0));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Totals.NodeCount.Should().Be(4);
        result.Totals.ElementCount.Should().Be(2);
        result.Totals.RigidCount.Should().Be(1);
        result.Totals.PointMassCount.Should().Be(1);
    }

    [Fact]
    public void LengthByCategory_SumsCorrectly()
    {
        var model = new FeModel();
        // Structure: N1(0,0,0)-N2(100,0,0) = 100mm
        // Pipe:      N3(0,0,0)-N4(200,0,0) = 200mm
        model.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 0, 200), N(4, 200, 200)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Structure));
        model.Elements.Add(E(11, 3, 4, EntityCategory.Pipe));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Totals.TotalLengthMm.Should().BeApproximately(300.0, 0.1);
        result.Totals.LengthByCategoryMm["Structure"].Should().BeApproximately(100.0, 0.1);
        result.Totals.LengthByCategoryMm["Pipe"].Should().BeApproximately(200.0, 0.1);
    }

    [Fact]
    public void BBox_ComputedFromAllNodes()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0, 0), N(2, 100, 50, 20)]);
        model.Elements.Add(E(10, 1, 2));

        var result = HealthMetricsAnalyzer.Analyze(model, null, DefaultTol);

        result.Totals.Bbox.MinX.Should().BeApproximately(0, 0.001);
        result.Totals.Bbox.MinY.Should().BeApproximately(0, 0.001);
        result.Totals.Bbox.MinZ.Should().BeApproximately(0, 0.001);
        result.Totals.Bbox.MaxX.Should().BeApproximately(100, 0.001);
        result.Totals.Bbox.MaxY.Should().BeApproximately(50, 0.001);
        result.Totals.Bbox.MaxZ.Should().BeApproximately(20, 0.001);
    }

    // ── DiagnosticCounts ──────────────────────────────────────────────────────

    [Fact]
    public void DiagnosticCounts_GroupedBySeverityAndCode()
    {
        var diags = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "CODE_X", "msg1"),
            new(DiagnosticSeverity.Warning, "CODE_X", "msg2"),
            new(DiagnosticSeverity.Info,    "CODE_Y", "msg3"),
            new(DiagnosticSeverity.Info,    "CODE_Y", "msg4"),
            new(DiagnosticSeverity.Info,    "CODE_Y", "msg5"),
        };

        var result = HealthMetricsAnalyzer.Analyze(new FeModel(), diags, DefaultTol);

        result.DiagnosticCounts.Warning.Should().Be(2);
        result.DiagnosticCounts.Info.Should().Be(3);
        result.DiagnosticCounts.Error.Should().Be(0);
        result.DiagnosticCounts.ByCode["CODE_X"].Should().Be(2);
        result.DiagnosticCounts.ByCode["CODE_Y"].Should().Be(3);
    }

    [Fact]
    public void DiagnosticCounts_NullDiagnostics_AllZero()
    {
        var result = HealthMetricsAnalyzer.Analyze(new FeModel(), null, DefaultTol);

        result.DiagnosticCounts.Error.Should().Be(0);
        result.DiagnosticCounts.Warning.Should().Be(0);
        result.DiagnosticCounts.Info.Should().Be(0);
        result.DiagnosticCounts.ByCode.Should().BeEmpty();
    }
}
