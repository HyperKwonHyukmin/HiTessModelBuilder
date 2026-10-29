using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

// centroid("spider") RBE2 가 만드는 새 GRID 노드 ID 가 기존 ID 와 충돌하지 않음을 보장하는 토대.
// ApplyAddRigidCentroid 는 SyncIdCounters() 직후 AllocNodeId() 로 새 노드 ID 를 받는다.
public class FeModelAllocNodeIdTests
{
    private static Node N(int id, double x = 0) => new(id, new Point3(x, 0, 0));

    [Fact]
    public void AllocNodeId_AfterSync_ReturnsMaxExistingIdPlusOne()
    {
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(7), N(33)]); // 비연속 ID, 최대 33
        m.SyncIdCounters();

        m.AllocNodeId().Should().Be(34, "새 노드 ID 는 기존 최대 ID + 1 이어야 충돌하지 않는다");
    }

    [Fact]
    public void AllocNodeId_IsMonotonic_NoCollisionAcrossMultipleAllocs()
    {
        // 한 run 에서 centroid intent 가 여러 번 적용돼도 새 노드 ID 끼리 충돌하지 않아야 한다.
        var m = new FeModel();
        m.Nodes.AddRange([N(1), N(2), N(5)]);
        m.SyncIdCounters();

        int a = m.AllocNodeId();
        int b = m.AllocNodeId();
        int c = m.AllocNodeId();

        a.Should().Be(6);
        b.Should().Be(7);
        c.Should().Be(8);
        new[] { a, b, c }.Should().OnlyHaveUniqueItems();
    }
}
