using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Analysis;

public class ModelDiagnosticsAnalyzerTests
{
    private static Node N(int id, double x, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Vector3.UnitZ);

    [Fact]
    public void Analyze_EmitsIndividualModelDiagnostics()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 100), N(3, 500), N(4, 501), N(5, 1000)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var diagnostics = ModelDiagnosticsAnalyzer.Analyze(model, new Tolerances(ShortElemMinMm: 5.0));

        diagnostics.Should().Contain(d => d.Code == "FREE_END_NODE" && d.NodeId == 1);
        diagnostics.Should().Contain(d => d.Code == "BEAM_ISOLATED_NODE" && d.NodeId == 5);
        diagnostics.Should().Contain(d => d.Code == "SHORT_ELEMENT" && d.ElemId == 11);
        diagnostics.Should().Contain(d => d.Code == "GROUP_NOT_CONNECTED" && d.GroupId == 1);
    }
}
