using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

public class TraceabilityTests
{
    // ── TraceEvent record ─────────────────────────────────────────────────────

    [Fact]
    public void TraceEvent_SameValues_AreEqual()
    {
        var a = new TraceEvent(TraceAction.ElementCreated, "initial", 1, null, null, null, "note");
        var b = new TraceEvent(TraceAction.ElementCreated, "initial", 1, null, null, null, "note");

        a.Should().Be(b);
    }

    [Fact]
    public void TraceEvent_DifferentAction_NotEqual()
    {
        var a = new TraceEvent(TraceAction.ElementCreated, "initial", 1, null, null, null, null);
        var b = new TraceEvent(TraceAction.ElementSplit,   "initial", 1, null, null, null, null);

        a.Should().NotBe(b);
    }

    // ── FeModel.TraceLog ──────────────────────────────────────────────────────

    [Fact]
    public void FeModel_TraceLog_EmptyByDefault()
    {
        new FeModel().TraceLog.Should().BeEmpty();
    }

    [Fact]
    public void AddTrace_Event_AppendsToLog()
    {
        var model = new FeModel();
        var ev    = new TraceEvent(TraceAction.NodeMerged, "NodeEquivalence", null, 5, null, 3, "merged");
        model.AddTrace(ev);

        model.TraceLog.Should().HaveCount(1);
        model.TraceLog[0].Should().Be(ev);
    }

    [Fact]
    public void AddTrace_Overload_CreatesCorrectEvent()
    {
        var model = new FeModel();
        model.AddTrace(TraceAction.ElementCreated, "initial", elementId: 7, note: "src");

        var ev = model.TraceLog[0];
        ev.Action.Should().Be(TraceAction.ElementCreated);
        ev.StageName.Should().Be("initial");
        ev.ElementId.Should().Be(7);
        ev.Note.Should().Be("src");
        ev.NodeId.Should().BeNull();
    }

    // ── SourceName defaults ───────────────────────────────────────────────────

    [Fact]
    public void BeamElement_SourceName_DefaultsToNull()
    {
        var elem = new BeamElement(1, 1, 2, 1, EntityCategory.Structure, Vector3.UnitZ);
        elem.SourceName.Should().BeNull();
        elem.ParentElementId.Should().BeNull();
    }

    [Fact]
    public void PointMass_SourceName_DefaultsToNull()
    {
        var pm = new PointMass(1, 1, 5.0);
        pm.SourceName.Should().BeNull();
    }

    [Fact]
    public void RigidElement_SourceName_DefaultsToNull()
    {
        var re = new RigidElement(1, 1, [2, 3]);
        re.SourceName.Should().BeNull();
    }

    [Fact]
    public void BeamElement_SourceName_StoredCorrectly()
    {
        var elem = new BeamElement(1, 1, 2, 1, EntityCategory.Structure, Vector3.UnitZ,
            sourceName: "=268454733", parentElementId: 42);
        elem.SourceName.Should().Be("=268454733");
        elem.ParentElementId.Should().Be(42);
    }
}
