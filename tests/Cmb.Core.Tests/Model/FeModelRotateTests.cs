using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

// FeModel 전체를 pivot 기준으로 강체 회전. 노드 좌표 + 빔 방향 벡터(CBEAM v-vector)를 함께 회전.
public class FeModelRotateTests
{
    private const double Tol = 1e-6;

    private static FeModel TwoNodeBeam()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(100, 0, 0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 1, EntityCategory.Structure, new Vector3(0, 0, 1)));
        return m;
    }

    [Fact]
    public void RotateAboutPivot_90AboutZ_AboutOrigin_RotatesNodePositions()
    {
        var m = TwoNodeBeam();

        m.RotateAboutPivot('Z', 90, new Point3(0, 0, 0));

        var n2 = m.FindNode(2)!;
        n2.Position.X.Should().BeApproximately(0, Tol);
        n2.Position.Y.Should().BeApproximately(100, Tol);
        n2.Position.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateAboutPivot_RotatesBeamOrientationVector()
    {
        var m = TwoNodeBeam();

        m.RotateAboutPivot('X', 90, new Point3(0, 0, 0));

        var e = m.Elements[0];
        e.Orientation.X.Should().BeApproximately(0, Tol);
        e.Orientation.Y.Should().BeApproximately(-1, Tol);
        e.Orientation.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateAboutPivot_PreservesNodeIdsAndTags()
    {
        var m = new FeModel();
        var n = new Node(7, new Point3(5, 0, 0));
        n.AddTag(NodeTags.Boundary);
        m.Nodes.Add(n);

        m.RotateAboutPivot('Z', 45, new Point3(0, 0, 0));

        var rotated = m.FindNode(7);
        rotated.Should().NotBeNull();
        rotated!.HasTag(NodeTags.Boundary).Should().BeTrue();
    }

    [Fact]
    public void RotateAboutPivot_360_IsNoOp()
    {
        var m = TwoNodeBeam();

        m.RotateAboutPivot('Z', 360, new Point3(0, 0, 0));

        var n2 = m.FindNode(2)!;
        n2.Position.X.Should().BeApproximately(100, Tol);
        n2.Position.Y.Should().BeApproximately(0, Tol);
        n2.Position.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateAboutPivot_PreservesPairwiseDistance()
    {
        var m = TwoNodeBeam();
        double before = m.FindNode(1)!.Position.DistanceTo(m.FindNode(2)!.Position);

        m.RotateAboutPivot('Y', 33.3, new Point3(12, 34, 56));

        double after = m.FindNode(1)!.Position.DistanceTo(m.FindNode(2)!.Position);
        after.Should().BeApproximately(before, Tol);
    }
}
