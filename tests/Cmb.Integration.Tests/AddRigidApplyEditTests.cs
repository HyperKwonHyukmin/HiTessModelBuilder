using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// 비-centroid addRigid(사용자가 독립 노드 + 종속 노드를 직접 지정) intent 가 apply-edit-intent 를
// 통해 반영될 때, 이미 다른 RBE2 의 GM 인 노드(occupied)는 새 RBE2 의 GM 에서 제외돼야 한다.
// (한 노드가 두 RBE2 의 GM 이면 Nastran "종속 DOF 중복 정의" FATAL) — centroid 경로와 동일한 불변식.
public class AddRigidApplyEditTests
{
    private const string BaseName = "06_Validation";

    [Fact]
    public void AddRigid_NonCentroid_ExcludesAlreadyOccupiedDeps()
    {
        var (tempDir, _) = PrepareSample();
        try
        {
            // 23,24 는 기존 RBE2(id=13) 의 GM 이므로 제외돼야 한다. 결과 GM = {3,4} 만 남아야 한다.
            int indep = 1;
            int[] selectedDeps = { 3, 4, 23, 24 };
            int[] expectedDeps = { 3, 4 };
            WriteNonCentroidEdit(tempDir, indep, selectedDeps, cm: "123456");
            RunApplyEditIntent(tempDir);

            var rigids = ReadRigids(Path.Combine(tempDir, "edited", BaseName + ".json"));

            var added = rigids.Single(r => r.indep == indep);
            added.deps.Should().BeEquivalentTo(expectedDeps, "occupied 노드(23,24)는 GM 에서 제외돼야 함");
            added.deps.Should().NotContain(23);
            added.deps.Should().NotContain(24);

            // 전역 불변식: 어떤 노드도 2개 이상 RBE2 의 GM 이 되어선 안 된다.
            var gmCounts = rigids
                .SelectMany(r => r.deps)
                .GroupBy(d => d)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            gmCounts.Should().BeEmpty("한 노드가 두 RBE2 의 GM 이면 Nastran FATAL");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- helpers (centroid 테스트와 동일 패턴) ----

    private static (string tempDir, Dictionary<int, (double x, double y, double z)> baseNodes) PrepareSample()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-addrigid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var sampleDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "hitess_mini", "20260617_082620"));
        File.Copy(Path.Combine(sampleDir, BaseName + ".json"), Path.Combine(tempDir, BaseName + ".json"));
        File.Copy(Path.Combine(sampleDir, BaseName + ".bdf"), Path.Combine(tempDir, BaseName + ".bdf"));

        var baseNodes = ReadNodes(Path.Combine(tempDir, BaseName + ".json"));
        baseNodes.Should().NotBeEmpty();
        return (tempDir, baseNodes);
    }

    private static void WriteNonCentroidEdit(string tempDir, int independentNode, int[] dependentNodes, string cm)
    {
        using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, BaseName + ".json")));
        var meta = stage.RootElement.GetProperty("meta");

        var paramsDict = new Dictionary<string, object?>
        {
            ["independentNode"] = independentNode,
            ["dependentNodes"] = dependentNodes,
            ["cm"] = cm,
        };

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
            intents = new[]
            {
                new
                {
                    id = Guid.NewGuid().ToString(),
                    kind = "addRigid",
                    createdAt = DateTimeOffset.UtcNow.ToString("o"),
                    @params = paramsDict,
                    validation = new { status = "ok", warnings = Array.Empty<string>(), errors = Array.Empty<string>() },
                },
            },
        };
        File.WriteAllText(
            Path.Combine(tempDir, BaseName + "_edit.json"),
            JsonSerializer.Serialize(editDocument));
    }

    private static void RunApplyEditIntent(string tempDir)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
        var start = new ProcessStartInfo("dotnet", $"\"{cliDll}\" apply-edit-intent \"{tempDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        process.WaitForExit(60_000).Should().BeTrue();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.ExitCode.Should().Be(0, $"stdout={stdout}{Environment.NewLine}stderr={stderr}");
    }

    private static Dictionary<int, (double x, double y, double z)> ReadNodes(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(
            n => n.GetProperty("id").GetInt32(),
            n => (n.GetProperty("x").GetDouble(), n.GetProperty("y").GetDouble(), n.GetProperty("z").GetDouble()));
    }

    private static List<(int id, int indep, int[] deps, string cm)> ReadRigids(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.GetProperty("rigids").EnumerateArray().Select(r => (
            r.GetProperty("id").GetInt32(),
            r.GetProperty("independentNode").GetInt32(),
            r.GetProperty("dependentNodes").EnumerateArray().Select(d => d.GetInt32()).ToArray(),
            r.GetProperty("cm").GetString() ?? "")).ToList();
    }
}
