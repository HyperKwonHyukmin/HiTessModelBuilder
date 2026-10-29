using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

// 장비 CSV 의 cog 가 엉뚱한 값(실례: 3592-35040 =30137/754429, 부착점에서 48m)이면
// COG 노드 ↔ 부착점 RBE2 가 수십 m 로 뻗는다. 모델은 그대로 두고 경고로 알린다.
public class EquipmentCogFarTests
{
    private static FeModel BuildModel(double cogX)
    {
        var model = new FeModel();
        model.Nodes.AddRange([
            new Node(1, new Point3(109730, -2999, 33032)),
            new Node(2, new Point3(111630, -2999, 33032)),
            new Node(100, new Point3(cogX, -2916, 33057)),
        ]);
        model.Elements.Add(new BeamElement(10, 1, 2, 1, EntityCategory.Structure, Vector3.UnitZ));
        model.Sections.Add(new BeamSection(1, BeamSectionKind.Tube, [100, 90], 1));
        model.Materials.Add(new Material(1, "Steel", 206000, 0.3, 7.85e-9));
        model.PointMasses.Add(new PointMass(900, 100, 0.715, "=30137/754429"));
        model.Rigids.Add(new RigidElement(500, 100, [1, 2], "EQUIPMENT", "=30137/754429"));
        return model;
    }

    [Fact]
    public void CogFarFromAttachments_IsWarned_AndModelKept()
    {
        var model = BuildModel(61449);
        var ctx = new StageContext(model, RunOptions.Default, NullLogger.Instance);

        new FinalValidationStage().Execute(ctx).Should().BeTrue();

        model.Rigids.Should().ContainSingle(r => r.Id == 500);
        model.PointMasses.Should().ContainSingle(p => p.NodeId == 100);
        var d = ctx.Diagnostics.Should().ContainSingle(x => x.Code == "EQUIPMENT_COG_FAR").Subject;
        d.Severity.Should().Be(DiagnosticSeverity.Warning);
        d.ElementId.Should().Be(500);
        d.NodeId.Should().Be(100);
        d.Message.Should().Contain("SourceName==30137/754429;")
            .And.Contain("distance=48281mm")
            .And.Contain("massKg=715");
    }

    [Fact]
    public void NormalCog_IsNotWarned()
    {
        var model = BuildModel(110700);
        var ctx = new StageContext(model, RunOptions.Default, NullLogger.Instance);

        new FinalValidationStage().Execute(ctx).Should().BeTrue();

        ctx.Diagnostics.Should().NotContain(x => x.Code == "EQUIPMENT_COG_FAR");
    }
}
