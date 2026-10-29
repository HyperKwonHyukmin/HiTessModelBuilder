using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// addRigid CENTROID("spider") intent 이 apply-edit-intent 를 통해 실제 수정 모델(JSON/BDF)에
// 반영되는지 검증. 선택 노드들의 산술 평균지점에 새 독립 GRID 가 생기고, 그 노드를 GN 으로 하는
// RBE2 가 만들어져야 한다. occupied(이미 다른 RBE2 의 GM) 노드는 제외되어야 한다.
public class AddRigidCentroidApplyEditTests
{
    private const string BaseName = "06_Validation";

    [Fact]
    public void AddRigidCentroid_CreatesSpiderRbeAtArithmeticMean()
    {
        var (tempDir, baseNodes) = PrepareSample();
        try
        {
            // 샘플의 노드 1,3,4 는 어느 기존 RBE2 의 GM 도 아니다(유일 RBE2 GM = {23,24}).
            int[] deps = { 1, 3, 4 };
            WriteCentroidEdit(tempDir, deps, cm: "123456");
            RunApplyEditIntent(tempDir);

            var editedNodes = ReadNodes(Path.Combine(tempDir, "edited", BaseName + ".json"));
            var rigids = ReadRigids(Path.Combine(tempDir, "edited", BaseName + ".json"));

            // 노드 1개 증가, 새 노드 ID = 기존 최대 ID + 1
            int baseMaxId = baseNodes.Keys.Max();
            editedNodes.Should().HaveCount(baseNodes.Count + 1);
            int newNodeId = baseMaxId + 1;
            editedNodes.Should().ContainKey(newNodeId);

            // 새 노드는 deps 의 산술 평균지점
            double ex = deps.Average(d => baseNodes[d].x);
            double ey = deps.Average(d => baseNodes[d].y);
            double ez = deps.Average(d => baseNodes[d].z);
            editedNodes[newNodeId].x.Should().BeApproximately(ex, 1e-6);
            editedNodes[newNodeId].y.Should().BeApproximately(ey, 1e-6);
            editedNodes[newNodeId].z.Should().BeApproximately(ez, 1e-6);

            // 새 노드를 GN 으로, deps 를 GM 으로 하는 RBE2 가 정확히 1개 추가
            var spider = rigids.Single(r => r.indep == newNodeId);
            spider.deps.Should().BeEquivalentTo(deps);
            spider.cm.Should().Be("123456");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddRigidCentroid_ExcludesAlreadyOccupiedDeps()
    {
        var (tempDir, baseNodes) = PrepareSample();
        try
        {
            // 23,24 는 기존 RBE2(id=13) 의 GM 이므로 제외돼야 하고, 평균지점도 [1,3,4] 로만 계산돼야 한다.
            int[] selected = { 1, 3, 4, 23, 24 };
            int[] expectedDeps = { 1, 3, 4 };
            WriteCentroidEdit(tempDir, selected, cm: "123456");
            RunApplyEditIntent(tempDir);

            var editedNodes = ReadNodes(Path.Combine(tempDir, "edited", BaseName + ".json"));
            var rigids = ReadRigids(Path.Combine(tempDir, "edited", BaseName + ".json"));

            int newNodeId = baseNodes.Keys.Max() + 1;
            var spider = rigids.Single(r => r.indep == newNodeId);
            spider.deps.Should().BeEquivalentTo(expectedDeps, "occupied 노드(23,24)는 GM 에서 제외");

            // 평균지점은 제외된 deps 가 아니라 [1,3,4] 로만 계산
            double ex = expectedDeps.Average(d => baseNodes[d].x);
            double ey = expectedDeps.Average(d => baseNodes[d].y);
            double ez = expectedDeps.Average(d => baseNodes[d].z);
            editedNodes[newNodeId].x.Should().BeApproximately(ex, 1e-6);
            editedNodes[newNodeId].y.Should().BeApproximately(ey, 1e-6);
            editedNodes[newNodeId].z.Should().BeApproximately(ez, 1e-6);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddRigidCentroid_WithMass_AddsConm2OnNewNode()
    {
        var (tempDir, baseNodes) = PrepareSample();
        try
        {
            int[] deps = { 1, 3, 4 };
            const double mass = 1.23;
            var basePointMassCount = ReadPointMasses(Path.Combine(tempDir, BaseName + ".json")).Count;

            WriteCentroidEdit(tempDir, deps, cm: "123456", mass: mass);
            RunApplyEditIntent(tempDir);

            int newNodeId = baseNodes.Keys.Max() + 1;
            var editedMasses = ReadPointMasses(Path.Combine(tempDir, "edited", BaseName + ".json"));
            editedMasses.Should().HaveCount(basePointMassCount + 1);

            var added = editedMasses.Single(pm => pm.nodeId == newNodeId);
            added.mass.Should().BeApproximately(mass, 1e-9);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddRigidCentroid_OverrideCoord_UsesProvidedCoordInsteadOfMean()
    {
        var (tempDir, baseNodes) = PrepareSample();
        try
        {
            int[] deps = { 1, 3, 4 };
            var coord = new { x = 1000.0, y = 2000.0, z = 3000.0 };
            WriteCentroidEdit(tempDir, deps, cm: "123456", independentCoord: coord);
            RunApplyEditIntent(tempDir);

            var editedNodes = ReadNodes(Path.Combine(tempDir, "edited", BaseName + ".json"));
            int newNodeId = baseNodes.Keys.Max() + 1;
            editedNodes[newNodeId].x.Should().BeApproximately(coord.x, 1e-6);
            editedNodes[newNodeId].y.Should().BeApproximately(coord.y, 1e-6);
            editedNodes[newNodeId].z.Should().BeApproximately(coord.z, 1e-6);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- helpers ----

    private static (string tempDir, Dictionary<int, (double x, double y, double z)> baseNodes) PrepareSample()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-centroid-" + Guid.NewGuid().ToString("N"));
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

    private static void WriteCentroidEdit(
        string tempDir, int[] dependentNodes, string cm, double? mass = null, object? independentCoord = null)
    {
        using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, BaseName + ".json")));
        var meta = stage.RootElement.GetProperty("meta");

        var paramsDict = new Dictionary<string, object?>
        {
            ["independentNode"] = null,
            ["dependentNodes"] = dependentNodes,
            ["createIndependentAtCentroid"] = true,
            ["cm"] = cm,
        };
        if (independentCoord is not null) paramsDict["independentCoord"] = independentCoord;
        if (mass is not null) paramsDict["mass"] = mass.Value;

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

    private static List<(int id, int nodeId, double mass)> ReadPointMasses(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.GetProperty("pointMasses").EnumerateArray().Select(pm => (
            pm.GetProperty("id").GetInt32(),
            pm.GetProperty("nodeId").GetInt32(),
            pm.GetProperty("mass").GetDouble())).ToList();
    }
}
