using System.Text.Json;
using FluentAssertions;
using static Cmb.Integration.Tests.DeleteRigidAddNodeApplyEditTests;

namespace Cmb.Integration.Tests;

// 요소·질량점 삭제와 고립 노드 정리(2026-10-28 기본 모델 편집 ②단계).
// 규칙은 Studio computeDeleteMask 와 같다: 정리 후보 노드는 모든 편집을 적용한 '최종 모델'에서
// 요소·RBE·질량 어디에도 안 걸린 것만 지운다(기록 순서 무관).
// 샘플 hitess_mini: E2 = N3–N4(N4 자유단, N3 은 E25 에도 걸림), 질량 M16 @ N26(E15 에 걸림).
public class DeleteEntitiesApplyEditTests
{
    [Fact]
    public void DeleteElements_WithOrphanCleanup_RemovesOnlyUnreferencedEndNode()
    {
        var result = Run(Intent("deleteElements", new { elementIds = new[] { 2 }, removeOrphanNodes = true }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var root = doc.RootElement;
        ElementIds(root).Should().NotContain(2);
        NodeIds(root).Should().NotContain(4).And.Contain(3, "N3 은 E25 가 아직 쓴다");
        File.ReadAllText(result.EditedBdf).Should().NotMatchRegex(@"(?m)^CBEAM\s+2\s");
    }

    [Fact]
    public void DeleteElements_WithoutCleanup_KeepsEndNodes()
    {
        var result = Run(Intent("deleteElements", new { elementIds = new[] { 2 }, removeOrphanNodes = false }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        NodeIds(doc.RootElement).Should().Contain(4);
    }

    [Fact]
    public void DeleteElements_NodeUsedByLaterRigid_IsKeptRegardlessOfOrder()
    {
        // RBE 를 먼저 기록해도, 나중에 기록해도 N4 는 RBE 가 쓰므로 남는다.
        var rbe = Intent("addRigid", new { independentNode = 4, dependentNodes = new[] { 9 }, cm = "123456" });
        var del = Intent("deleteElements", new { elementIds = new[] { 2 }, removeOrphanNodes = true });
        foreach (var order in new[] { new[] { rbe, del }, new[] { del, rbe } })
        {
            var result = Run(order);
            result.ExitCode.Should().Be(0, result.Log);
            using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
            NodeIds(doc.RootElement).Should().Contain(4);
        }
    }

    [Fact]
    public void DeleteMasses_RemovesMassButKeepsStructuralNode()
    {
        var result = Run(Intent("deleteMasses", new { massIds = new[] { 16 } }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var root = doc.RootElement;
        root.GetProperty("pointMasses").EnumerateArray().Select(m => m.GetProperty("id").GetInt32())
            .Should().NotContain(16);
        NodeIds(root).Should().Contain(26);
    }

    [Fact]
    public void DeleteOrphanNodes_OnlyRemovesNodesUnreferencedInFinalModel()
    {
        // 정리만 하면 N4 는 E2 가 쓰므로 남고, E2 를 (정리 없이) 지운 뒤 정리하면 사라진다.
        var keep = Run(Intent("deleteOrphanNodes", new { nodeIds = new[] { 4 } }));
        keep.ExitCode.Should().Be(0, keep.Log);
        using (var doc = JsonDocument.Parse(File.ReadAllText(keep.EditedJson)))
            NodeIds(doc.RootElement).Should().Contain(4);

        var gone = Run(
            Intent("deleteOrphanNodes", new { nodeIds = new[] { 4 } }),
            Intent("deleteElements", new { elementIds = new[] { 2 }, removeOrphanNodes = false }));
        gone.ExitCode.Should().Be(0, gone.Log);
        using (var doc = JsonDocument.Parse(File.ReadAllText(gone.EditedJson)))
            NodeIds(doc.RootElement).Should().NotContain(4);
    }

    [Theory]
    [InlineData("deleteElements", "elementIds")]
    [InlineData("deleteMasses", "massIds")]
    [InlineData("deleteOrphanNodes", "nodeIds")]
    public void UnknownIds_AreRejected(string kind, string key)
    {
        var result = Run(Intent(kind, new Dictionary<string, object> { [key] = new[] { 99999 } }));
        result.ExitCode.Should().Be(65, result.Log);
    }

    private static List<int> ElementIds(JsonElement root) =>
        root.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
}
