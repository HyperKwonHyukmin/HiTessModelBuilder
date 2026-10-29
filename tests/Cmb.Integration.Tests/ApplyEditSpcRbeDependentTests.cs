using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// apply-edit-intent 는 기준 BDF 의 SPC1 노드를 이어받는다. 편집으로 새로 생긴 RBE2 의 종속 노드가
// 그 안에 있으면 한 노드가 UM(종속)·US(구속) 에 동시에 들어가 Nastran FATAL 2101 이 난다.
// build-full 의 SPC 시딩과 같이 RBE2 종속 노드는 SPC 에서 빼야 한다.
// (2026-09-23 GroupModuleUnit 샘플: 사다리 하단 SPC 노드 385/387/429/431 을 자동 연결 RBE 로 이었을 때 24건)
public class ApplyEditSpcRbeDependentTests
{
    [Fact]
    public void PreservedSpc_ExcludesRbeDependentNodes()
    {
        var model = new FeModel();
        model.Nodes.AddRange([
            new Node(1, new Point3(0, 0, 0)),
            new Node(2, new Point3(100, 0, 0)),
            new Node(3, new Point3(200, 0, 0)),
        ]);
        model.Rigids.Add(new RigidElement(10, 3, [2], "AUTOCONNECT"));   // 편집으로 추가된 RBE: N2 가 종속

        var bdf = Path.Combine(Path.GetTempPath(), $"cmb-spc-{Guid.NewGuid():N}.bdf");
        try
        {
            File.WriteAllLines(bdf, [
                "BEGIN BULK",
                "SPC1           1  123456       1       2",
                "ENDDATA",
            ]);

            var spc = EditIntentApplier.ReadSpcNodeIdsFromBdf(bdf, model);

            spc.Should().BeEquivalentTo([1], "RBE2 종속 노드(N2)는 SPC 에서 빠져야 한다 — FATAL 2101");
        }
        finally
        {
            File.Delete(bdf);
        }
    }
}
