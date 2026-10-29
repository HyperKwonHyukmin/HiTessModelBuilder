using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// deleteRigid · addNode 를 C# 엔진이 직접 적용하는지 검증(2026-10-28 MB 경로 엔진 단일화).
// 예전엔 엔진이 두 kind 를 몰라 exit 65 → nastran_bridge 가 intent 전체를 대신 적용했다.
// 규칙은 bridge(apply_edit_json)와 같다: removeMass 면 독립 노드 질량 제거, 고립된 노드 정리,
// addNode 는 그 GRID 를 참조하는 addRigid 보다 먼저 적용.
// 샘플 hitess_mini: RBE 13(독립 N22 = 질량 노드, 종속 N23·N24), N1 은 RBE 에 안 걸린 자유단.
public class DeleteRigidAddNodeApplyEditTests
{
    private const string BaseName = "06_Validation";

    [Fact]
    public void DeleteRigid_WithRemoveMass_RemovesRigidMassAndOrphanHub()
    {
        var result = Run(Intent("deleteRigid", new { rigidId = 13, reason = "equipmentCogFar", removeMass = true }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var root = doc.RootElement;
        root.GetProperty("rigids").EnumerateArray().Select(r => r.GetProperty("id").GetInt32())
            .Should().NotContain(13);
        root.GetProperty("pointMasses").EnumerateArray().Select(m => m.GetProperty("nodeId").GetInt32())
            .Should().NotContain(22);
        NodeIds(root).Should().NotContain(22, "질량·RBE 가 모두 빠진 COG 노드는 자유 GRID 라 지운다");
    }

    [Fact]
    public void DeleteRigid_WithoutRemoveMass_KeepsMassAndItsNode()
    {
        var result = Run(Intent("deleteRigid", new { rigidId = 13, reason = "mergeRigid" }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var root = doc.RootElement;
        root.GetProperty("pointMasses").EnumerateArray().Select(m => m.GetProperty("nodeId").GetInt32())
            .Should().Contain(22);
        NodeIds(root).Should().Contain(22, "질량이 걸린 노드는 고립이 아니다");
    }

    [Fact]
    public void AddNode_ThenAddRigidReferencingIt_AppliesEvenWhenRecordedAfter()
    {
        // addRigid 를 배열 앞에 둬도 addNode 가 먼저 적용되어야 한다.
        var result = Run(
            Intent("addRigid", new { independentNode = 900, dependentNodes = new[] { 1 }, cm = "123456", remark = "test" }),
            Intent("addNode", new { nodeId = 900, x = 1000.5, y = -20.0, z = 300.0, remark = "test" }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var root = doc.RootElement;
        var added = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == 900);
        added.GetProperty("x").GetDouble().Should().BeApproximately(1000.5, 1e-6);
        added.GetProperty("y").GetDouble().Should().BeApproximately(-20.0, 1e-6);
        added.GetProperty("z").GetDouble().Should().BeApproximately(300.0, 1e-6);
        root.GetProperty("rigids").EnumerateArray()
            .Should().Contain(r => r.GetProperty("independentNode").GetInt32() == 900);
        File.ReadAllText(result.EditedBdf).Should().Contain("900");
    }

    [Fact]
    public void AddNode_WithExistingId_IsRejected()
    {
        var result = Run(Intent("addNode", new { nodeId = 1, x = 0.0, y = 0.0, z = 0.0 }));
        result.ExitCode.Should().Be(65, result.Log);
    }

    [Fact]
    public void DeleteRigid_UnknownId_IsRejected()
    {
        var result = Run(Intent("deleteRigid", new { rigidId = 99999 }));
        result.ExitCode.Should().Be(65, result.Log);
    }

    internal static object Intent(string kind, object parameters) => new
    {
        id = Guid.NewGuid().ToString(),
        kind,
        createdAt = DateTimeOffset.UtcNow.ToString("o"),
        @params = parameters,
        validation = new { status = "ok", warnings = Array.Empty<string>(), errors = Array.Empty<string>() },
    };

    internal static List<int> NodeIds(JsonElement root) =>
        root.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetInt32()).ToList();

    internal sealed record RunResult(int ExitCode, string Log, string EditedJson, string EditedBdf);

    internal static RunResult Run(params object[] intents)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-delrigid-" + Guid.NewGuid().ToString("N"));
        var keepDir = Path.Combine(Path.GetTempPath(), "cmb-delrigid-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sampleDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "samples", "hitess_mini", "20260617_082620"));
            File.Copy(Path.Combine(sampleDir, BaseName + ".json"), Path.Combine(tempDir, BaseName + ".json"));
            File.Copy(Path.Combine(sampleDir, BaseName + ".bdf"), Path.Combine(tempDir, BaseName + ".bdf"));

            using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, BaseName + ".json")));
            var meta = stage.RootElement.GetProperty("meta");
            var editDocument = new
            {
                schemaVersion = "1.0",
                stageRef = new
                {
                    phase = meta.GetProperty("phase").GetString(),
                    stageName = meta.GetProperty("stageName").GetString(),
                    sourceTimestamp = meta.GetProperty("timestamp").GetString(),
                },
                createdAt = DateTimeOffset.UtcNow.ToString("o"),
                createdBy = "integration-test",
                intents,
            };
            File.WriteAllText(Path.Combine(tempDir, BaseName + "_edit.json"), JsonSerializer.Serialize(editDocument));

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            var start = new ProcessStartInfo("dotnet", $"\"{cliDll}\" apply-edit-intent \"{tempDir}\" --out \"{keepDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
            process.WaitForExit(60_000).Should().BeTrue();
            var log = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
            return new RunResult(process.ExitCode, log,
                Path.Combine(keepDir, BaseName + ".json"), Path.Combine(keepDir, BaseName + ".bdf"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
