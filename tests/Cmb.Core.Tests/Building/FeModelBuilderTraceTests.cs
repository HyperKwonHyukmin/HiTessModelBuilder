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
        var rows = new[]
        {
            new RawEquipRow("EQ1", [0, 0, 0], [100, 0, 0], 5.0),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], [], rows, []));

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

        var ev = model.TraceLog[0];
        ev.Action.Should().Be(TraceAction.ElementCreated);
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
}
