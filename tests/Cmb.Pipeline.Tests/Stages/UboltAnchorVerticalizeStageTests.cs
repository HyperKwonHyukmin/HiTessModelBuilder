using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

// M-3: U-bolt 앵커 수직화 스테이지의 no-op 경로 골든/스모크 테스트.
public class UboltAnchorVerticalizeStageTests
{
    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    [Fact]
    public void EmptyModel_ReturnsTrue_NoVerticalizeDiagnostic()
    {
        var ctx = MakeCtx(new FeModel());

        var result = new UboltAnchorVerticalizeStage().Execute(ctx);

        result.Should().BeTrue();
        ctx.Diagnostics.Should().NotContain(d => d.Code == "UBOLT_ANCHOR_VERTICALIZED");
    }

    [Fact]
    public void ModelWithoutUbolts_IsNoOpAndReturnsTrue()
    {
        // U-bolt RBE2 가 없으면 수직화 대상이 없어 no-op(진단 없음)으로 성공한다.
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(1000, 0, 0)));
        m.Elements.Add(new BeamElement(10, 1, 2, 1, EntityCategory.Pipe, Vector3.UnitZ));
        m.Sections.Add(new BeamSection(1, BeamSectionKind.Tube, [100, 5], 1));
        m.Materials.Add(Material.DefaultSteel);

        var ctx = MakeCtx(m);
        var result = new UboltAnchorVerticalizeStage().Execute(ctx);

        result.Should().BeTrue();
        ctx.Diagnostics.Should().NotContain(d => d.Code == "UBOLT_ANCHOR_VERTICALIZED");
    }
}
