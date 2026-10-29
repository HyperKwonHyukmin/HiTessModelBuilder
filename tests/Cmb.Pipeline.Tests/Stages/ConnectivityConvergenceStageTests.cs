using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

// M-3: 연결성 수렴 루프가 자명한 입력에서 안전하게 종료(수렴)하는지에 대한 골든/스모크 테스트.
public class ConnectivityConvergenceStageTests
{
    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x, double y = 0, double z = 0) => new(id, new Point3(x, y, z));
    private static BeamElement E(int id, int a, int b) => new(id, a, b, 1, EntityCategory.Structure, Vector3.UnitZ);

    [Fact]
    public void EmptyModel_ConvergesAndReturnsTrue()
    {
        var ctx = MakeCtx(new FeModel());

        var result = new ConnectivityConvergenceStage().Execute(ctx);

        result.Should().BeTrue();
        ctx.Diagnostics.Should().NotContain(d => d.Code == "CONNECTIVITY_CONVERGENCE_LIMIT");
    }

    [Fact]
    public void SingleConnectedChain_ConvergesWithoutIterationLimitWarning()
    {
        // 이미 단일 연결 그룹 → GroupConnect 이동 0 → 1회 반복 후 수렴, 반복 한계 경고 없음.
        var m = new FeModel();
        m.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 200)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);
        m.Sections.Add(new BeamSection(1, BeamSectionKind.H, [400, 200, 13, 21], 1));
        m.Materials.Add(new Material(1, "Steel", 206000, 0.3, 7.85e-9));

        var ctx = MakeCtx(m);
        var result = new ConnectivityConvergenceStage().Execute(ctx);

        result.Should().BeTrue();
        ctx.Diagnostics.Should().NotContain(d => d.Code == "CONNECTIVITY_CONVERGENCE_LIMIT");
    }
}
