using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class BoxUboltConnectionStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, int propertyId, EntityCategory category)
        => new(id, start, end, propertyId, category, Up);

    [Fact]
    public void BoxUbolt_ConnectsFourDirections()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [100, 10, 100, 10], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.Tube, [50, 45], 1));

        model.Nodes.AddRange([
            N(1, -100,  100,   0), N(2, 100,  100,   0),
            N(3, -100, -100,   0), N(4, 100, -100,   0),
            N(5, -100,    0, 100), N(6, 100,    0, 100),
            N(7, -100,    0,-100), N(8, 100,    0,-100),
            N(9,    0,    0,   0), N(10, 100,   0,   0)
        ]);
        model.Elements.AddRange([
            E(11, 1, 2, 1, EntityCategory.Structure),
            E(12, 3, 4, 1, EntityCategory.Structure),
            E(13, 5, 6, 1, EntityCategory.Structure),
            E(14, 7, 8, 1, EntityCategory.Structure),
            E(15, 9, 10, 2, EntityCategory.Pipe)
        ]);
        model.Rigids.Add(new RigidElement(20, 9, [], "UBOLT", cm: "123456", detailRemark: "BOX"));

        new BoxUboltConnectionStage().Execute(MakeCtx(model));

        var rbe = model.Rigids.First(r => r.Id == 20);
        rbe.DependentNodeIds.Should().HaveCount(4);
        rbe.DetailRemark.Should().Be("BOX");
        rbe.Cm.Should().Be("123456");
    }

    [Fact]
    public void BoxUbolt_DoesNotMarkBoundary_WhenNoCandidateFound()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.Tube, [50, 45], 1));
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        model.Elements.Add(E(10, 1, 2, 1, EntityCategory.Pipe));
        model.Rigids.Add(new RigidElement(20, 1, [], "UBOLT", detailRemark: "BOX"));

        var ctx = MakeCtx(model);
        new BoxUboltConnectionStage().Execute(ctx);

        model.Rigids.First(r => r.Id == 20).DependentNodeIds.Should().BeEmpty();
        model.Nodes.First(n => n.Id == 1).HasTag(NodeTags.Boundary).Should().BeFalse();
        ctx.Diagnostics.Should().Contain(d => d.Code == "BOX_UBOLT_NO_STRUCTURE");
    }

    [Fact]
    public void BoxUbolt_PreSnapsIndepOntoPipe_WhenIndepOffsetByCsvRoundingNoise()
    {
        // CSV sub-mm 좌표 오차로 BOX UBOLT indep가 pipe centerline에서 0.7mm 떨어진 케이스.
        // SplitByExistingNodes(NodeOnSegmentTolMm=0.5mm) 에 잡히지 않으므로
        // BoxUboltConnectionStage 진입 시 pipe owner 가 없는 상태이며, pre-snap 으로 복구되어야 한다.
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [100, 10, 100, 10], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.Tube, [50, 45], 1));

        model.Nodes.AddRange([
            N(1, -100,  100,   0), N(2, 100,  100,   0),
            N(3, -100, -100,   0), N(4, 100, -100,   0),
            N(5, -100,    0, 100), N(6, 100,    0, 100),
            N(7, -100,    0,-100), N(8, 100,    0,-100),
            // pipe endpoints — UBOLT(50)이 어떤 endpoint도 아니어야 함
            N(20, -100,  0, 0), N(21, 100, 0, 0),
            // BOX UBOLT indep — pipe centerline(z=0)에서 0.7mm 이탈
            N(50,   0,  0, 0.7)
        ]);
        model.Elements.AddRange([
            E(11, 1, 2, 1, EntityCategory.Structure),
            E(12, 3, 4, 1, EntityCategory.Structure),
            E(13, 5, 6, 1, EntityCategory.Structure),
            E(14, 7, 8, 1, EntityCategory.Structure),
            E(15, 20, 21, 2, EntityCategory.Pipe)
        ]);
        model.Rigids.Add(new RigidElement(60, 50, [], "UBOLT", cm: "123456", detailRemark: "BOX"));

        var ctx = MakeCtx(model);
        new BoxUboltConnectionStage().Execute(ctx);

        var rbe = model.Rigids.First(r => r.Id == 60);
        rbe.DependentNodeIds.Should().HaveCount(4, "pre-snap 후 4 방향 structure 가 모두 잡혀야 함");
        rbe.IndependentNodeId.Should().NotBe(50, "indep는 pipe centerline 위 새 노드로 교체되어야 함");

        var newIndep = model.Nodes.First(n => n.Id == rbe.IndependentNodeId);
        newIndep.Position.Z.Should().BeApproximately(0, 1e-6);
        newIndep.Position.X.Should().BeApproximately(0, 1e-6);
        newIndep.Position.Y.Should().BeApproximately(0, 1e-6);

        // 원본 pipe (E15) 가 분할되어 새 indep 가 endpoint 인 두 child element 가 존재해야 함
        var pipesAtIndep = model.Elements.Where(e =>
            e.Category == EntityCategory.Pipe &&
            (e.StartNodeId == rbe.IndependentNodeId || e.EndNodeId == rbe.IndependentNodeId)).ToList();
        pipesAtIndep.Should().HaveCount(2);

        ctx.Diagnostics.Should().Contain(d => d.Code == "BOX_UBOLT_PRE_SNAPPED");
    }

    [Fact]
    public void BoxUbolt_SideCandidateInsidePipeRadius_IsPreferredOverDiagonalOutsideBox()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.L, [90, 90, 10, 10], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.Tube, [136.5, 132.31], 1));

        model.Nodes.AddRange([
            N(1,    0,  50, -100), N(2,   0,  50, 100),  // local-left side support
            N(3, -100, 350, -320), N(4, 100, 350, -320), // diagonal/outside candidate
            N(9,    0,   0,    0), N(10, 100,   0,   0)
        ]);
        model.Elements.AddRange([
            E(11, 1, 2, 1, EntityCategory.Structure),
            E(12, 3, 4, 1, EntityCategory.Structure),
            E(15, 9, 10, 2, EntityCategory.Pipe)
        ]);
        model.Rigids.Add(new RigidElement(20, 9, [], "UBOLT", cm: "13", detailRemark: "BOX"));

        new BoxUboltConnectionStage().Execute(MakeCtx(model));

        var depId = model.Rigids.First(r => r.Id == 20).DependentNodeIds.Should().ContainSingle().Subject;
        var dep = model.Nodes.First(n => n.Id == depId);
        dep.Position.Y.Should().BeApproximately(50, 1e-6);
        dep.Position.Z.Should().BeApproximately(0, 1e-6);
    }
}
