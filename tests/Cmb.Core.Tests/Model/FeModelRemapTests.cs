using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

public class FeModelRemapTests
{
    private static Node N(int id, double x = 0) => new(id, new Point3(x, 0, 0));

    [Fact]
    public void RemapsRigidIndepAndDep()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(3), N(100)]);
        m.Rigids.Add(new RigidElement(10, 1, [2, 3]));

        m.RemapNodeReferences(new Dictionary<int, int> { [1] = 100 });

        m.Rigids.Should().ContainSingle();
        m.Rigids[0].IndependentNodeId.Should().Be(100);
        m.Rigids[0].DependentNodeIds.Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void RemapsPointMassNodeId()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(100)]);
        m.PointMasses.Add(new PointMass(10, 1, 5.0));

        m.RemapNodeReferences(new Dictionary<int, int> { [1] = 100 });

        m.PointMasses.Should().ContainSingle();
        m.PointMasses[0].NodeId.Should().Be(100);
    }

    [Fact]
    public void DeletesRigidWhenAllDepsCollapseToIndep()
    {
        // dep와 indep이 동일 노드로 매핑되면 dep 리스트 비어 → RBE 삭제
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2)]);
        m.Rigids.Add(new RigidElement(10, 1, [2]));

        m.RemapNodeReferences(new Dictionary<int, int> { [2] = 1 }); // dep → indep와 같아짐

        m.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void EmptyMap_NoChange()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2)]);
        m.Rigids.Add(new RigidElement(10, 1, [2]));
        m.PointMasses.Add(new PointMass(20, 1, 1.0));

        m.RemapNodeReferences(new Dictionary<int, int>());

        m.Rigids.Should().ContainSingle();
        m.Rigids[0].IndependentNodeId.Should().Be(1);
        m.PointMasses.Should().ContainSingle();
        m.PointMasses[0].NodeId.Should().Be(1);
    }
}
