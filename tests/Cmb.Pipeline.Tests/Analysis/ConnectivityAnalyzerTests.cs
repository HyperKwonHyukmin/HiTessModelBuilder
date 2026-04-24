using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Analysis;

public class ConnectivityAnalyzerTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static Node N(int id, double x = 0, double y = 0)
        => new(id, new Point3(x, y, 0));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Up);

    // ── 빈 모델 ────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyModel_Returns_ZeroGroups()
    {
        var result = ConnectivityAnalyzer.Analyze(new FeModel());

        result.GroupCount.Should().Be(0);
        result.LargestGroupElementCount.Should().Be(0);
        result.LargestGroupNodeRatio.Should().Be(0.0);
        result.Groups.Should().BeEmpty();
    }

    // ── 단일 체인 — 완전 연결 ──────────────────────────────────────────────────

    [Fact]
    public void SingleChain_Returns_OneGroup_FullRatio()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);

        var result = ConnectivityAnalyzer.Analyze(model);

        result.GroupCount.Should().Be(1);
        result.LargestGroupElementCount.Should().Be(2);
        result.LargestGroupNodeRatio.Should().Be(1.0);
        result.IsolatedNodeCount.Should().Be(0);
    }

    // ── 두 개 분리 체인 ────────────────────────────────────────────────────────

    [Fact]
    public void TwoDisconnectedChains_Returns_TwoGroups()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 500, 0), N(4, 600, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var result = ConnectivityAnalyzer.Analyze(model);

        result.GroupCount.Should().Be(2);
        result.LargestGroupElementCount.Should().Be(1);
    }

    // ── 최대 그룹이 정확히 식별됨 ──────────────────────────────────────────────

    [Fact]
    public void LargestGroupIdentifiedCorrectly()
    {
        // 그룹A: 요소 3개 (N1-N2-N3-N4), 그룹B: 요소 1개 (N5-N6)
        var model = new FeModel();
        model.Nodes.AddRange([N(1), N(2), N(3), N(4), N(5, 500), N(6, 600)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3), E(12, 3, 4), E(13, 5, 6)]);

        var result = ConnectivityAnalyzer.Analyze(model);

        result.GroupCount.Should().Be(2);
        result.LargestGroupElementCount.Should().Be(3);
        result.LargestGroupNodeCount.Should().Be(4);
        result.Groups[0].ElementCount.Should().Be(3);
        result.Groups[1].ElementCount.Should().Be(1);
    }

    // ── LargestGroupNodeRatio 계산 정확성 ─────────────────────────────────────

    [Fact]
    public void LargestGroupNodeRatio_CalculatedFromElementNodeCount()
    {
        // 그룹A: 노드 2개 (N1,N2), 그룹B: 노드 2개 (N3,N4) → 최대비율 = 2/4 = 0.5
        var model = new FeModel();
        model.Nodes.AddRange([N(1), N(2), N(3, 500), N(4, 600)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var result = ConnectivityAnalyzer.Analyze(model);

        result.LargestGroupNodeRatio.Should().BeApproximately(0.5, 0.001);
    }

    // ── 고립 노드(요소에 미참조) 카운트 ──────────────────────────────────────

    [Fact]
    public void OrphanNode_Counted_In_IsolatedNodeCount()
    {
        var model = new FeModel();
        // N3는 어떤 요소에도 포함되지 않는 고립 노드
        model.Nodes.AddRange([N(1), N(2), N(3, 999, 999)]);
        model.Elements.Add(E(10, 1, 2));

        var result = ConnectivityAnalyzer.Analyze(model);

        result.IsolatedNodeCount.Should().Be(1);
        result.GroupCount.Should().Be(1);
    }

    // ── 그룹 ID가 요소 수 내림차순으로 1부터 순번 부여 ───────────────────────

    [Fact]
    public void Groups_AreNumbered_ByElementCountDescending()
    {
        // 그룹A: 2요소, 그룹B: 1요소
        var model = new FeModel();
        model.Nodes.AddRange([N(1), N(2), N(3), N(4, 500), N(5, 600)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3), E(12, 4, 5)]);

        var result = ConnectivityAnalyzer.Analyze(model);

        result.Groups.Should().HaveCount(2);
        result.Groups[0].Id.Should().Be(1);
        result.Groups[0].ElementCount.Should().BeGreaterThan(result.Groups[1].ElementCount);
        result.Groups[1].Id.Should().Be(2);
    }
}
