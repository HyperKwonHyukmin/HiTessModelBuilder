using Cmb.Core.Building;
using Cmb.Core.Model;
using Cmb.Core.Model.Raw;
using FluentAssertions;

namespace Cmb.Core.Tests.Building;

public class FeModelBuilderTraceTests
{
    // ── SourceName on elements ────────────────────────────────────────────────

    [Fact]
    public void Build_StructureBeam_SourceNameEqualsRowName()
    {
        var rows = new[]
        {
            new RawBeamRow("MY_BEAM", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], ""),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Elements[0].SourceName.Should().Be("MY_BEAM");
    }

    [Fact]
    public void Build_TwoDifferentBeams_EachHasOwnSourceName()
    {
        var rows = new[]
        {
            new RawBeamRow("BEAM_A", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], ""),
            new RawBeamRow("BEAM_B", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [2000, 0, 0], [3000, 0, 0], [0, 0, 1], ""),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Elements[0].SourceName.Should().Be("BEAM_A");
        model.Elements[1].SourceName.Should().Be("BEAM_B");
    }

    [Fact]
    public void Build_EquipRow_PointMassHasSourceName()
    {
        var beams = new[]
        {
            new RawBeamRow("SUPPORT", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [100, 0, 0], [200, 0, 0], [0, 0, 1], ""),
        };
        var rows = new[]
        {
            new RawEquipRow("EQ1", [0, 0, 0], [100, 0, 0], 5.0),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(beams, [], rows, []));

        model.PointMasses[0].SourceName.Should().Be("EQ1");
    }

    // ── TraceLog ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ThreeBeams_TraceLogHasThreeCreatedEvents()
    {
        var rows = new[]
        {
            new RawBeamRow("A", "BEAM", "BEAM_200x100x8x12", [200, 100, 8, 12],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], ""),
            new RawBeamRow("B", "BEAM", "BEAM_200x100x8x12", [200, 100, 8, 12],
                [2000, 0, 0], [3000, 0, 0], [0, 0, 1], ""),
            new RawBeamRow("C", "ANG", "ANG_75x75x9", [75, 75, 9],
                [5000, 0, 0], [6000, 0, 0], [0, 0, 1], ""),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var created = model.TraceLog.Where(t => t.Action == TraceAction.ElementCreated).ToList();
        created.Should().HaveCount(3);
    }

    [Fact]
    public void Build_TraceLog_ElementCreated_NoteEqualsSourceName()
    {
        var rows = new[]
        {
            new RawBeamRow("MY_STRUCT", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], ""),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var ev = model.TraceLog.First(t => t.Action == TraceAction.ElementCreated);
        ev.StageName.Should().Be("initial");
        ev.Note.Should().Be("MY_STRUCT");
        ev.ElementId.Should().Be(model.Elements[0].Id);
    }

    [Fact]
    public void Build_EmptyInput_TraceLogEmpty()
    {
        var model = new FeModelBuilder().Build(new RawDesignData([], [], [], []));
        model.TraceLog.Should().BeEmpty();
    }

    // ── F-8(3): 부착점을 못 찾아 장비 질량이 조용히 누락되면 skip 트레이스로 가시화한다 ──

    [Fact]
    public void Build_EquipmentWithNoValidMountNode_RecordsSkipTraceForMassLoss()
    {
        // 부착 대상 노드가 하나도 없으므로(요소 없음) CoG 10mm 내 유효 노드를 못 찾아 질량이 누락된다.
        var equips = new[] { new RawEquipRow("EQ_LOST", [0, 0, 0], [999999, 0, 0], 5.0) };
        var model = new FeModelBuilder().Build(new RawDesignData([], [], equips, []));

        model.PointMasses.Should().BeEmpty();
        var skipped = model.TraceLog.Where(t => t.Action == TraceAction.ElementSkipped).ToList();
        skipped.Should().ContainSingle();
        skipped[0].Note.Should().Contain("EQ_LOST");
    }

    [Fact]
    public void Build_EquipmentInterPosNoValidMount_RecordsSkipTrace()
    {
        // InterPos 경로에서도 유효 dependent 노드를 못 찾으면 RBE2/질량이 통째로 누락 → skip 트레이스.
        var equips = new[]
        {
            new RawEquipRow("EQ_RBE_LOST", [0, 0, 0], [0, 0, 0], 5.0, InterPos: [999999, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], [], equips, []));

        model.PointMasses.Should().BeEmpty();
        model.Rigids.Should().BeEmpty();
        model.TraceLog.Where(t => t.Action == TraceAction.ElementSkipped)
            .Should().ContainSingle(t => t.Note != null && t.Note.Contains("EQ_RBE_LOST"));
    }

    [Fact]
    public void Build_NodesAreTraced_NodeCreatedEvents()
    {
        var rows = new[]
        {
            new RawBeamRow("A", "BEAM", "BEAM_200x100x8x12", [200, 100, 8, 12],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], ""),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var nodeCreated = model.TraceLog.Where(t => t.Action == TraceAction.NodeCreated).ToList();
        nodeCreated.Should().HaveCount(model.Nodes.Count);
        nodeCreated.Should().OnlyContain(t => t.StageName == "initial");
        nodeCreated.Select(t => t.NodeId).Should().BeEquivalentTo(model.Nodes.Select(n => (int?)n.Id));
    }
}
