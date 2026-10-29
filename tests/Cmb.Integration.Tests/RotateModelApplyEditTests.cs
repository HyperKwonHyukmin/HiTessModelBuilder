using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// rotateModel intent 이 apply-edit-intent 를 통해 실제 수정 모델(JSON/BDF)에 회전을 반영하는지 검증.
// Z축 회전이므로 모든 노드의 Z 좌표는 보존되어야 하고, 강체 회전이므로 노드 간 거리는 보존되어야 한다.
public class RotateModelApplyEditTests
{
    private const string BaseName = "06_Validation";

    [Fact]
    public void RotateModel_90AboutZ_RotatesEditedJsonNodes()
    {
        var (baseNodes, editedNodes) = RunApplyEdit(new { axis = "Z", angleDeg = 90.0 });
        editedNodes.Should().HaveCount(baseNodes.Count);

        // Z축 회전 → 모든 노드의 Z 보존
        foreach (var (id, p) in editedNodes)
            p.z.Should().BeApproximately(baseNodes[id].z, 1e-3, $"node {id} Z must be preserved under Z rotation");

        // 실제로 회전이 일어났는지: 적어도 한 노드의 X 또는 Y 가 바뀜
        editedNodes.Any(kv =>
            Math.Abs(kv.Value.x - baseNodes[kv.Key].x) > 1.0 ||
            Math.Abs(kv.Value.y - baseNodes[kv.Key].y) > 1.0).Should().BeTrue("rotation must move nodes in XY");

        // 강체 회전 → 임의 두 노드 간 거리 보존
        var ids = baseNodes.Keys.OrderBy(x => x).Take(2).ToArray();
        ids.Should().HaveCount(2);
        double before = Dist(baseNodes[ids[0]], baseNodes[ids[1]]);
        double after = Dist(editedNodes[ids[0]], editedNodes[ids[1]]);
        after.Should().BeApproximately(before, 1e-3);
    }

    // Studio 가 기록한 pivot 을 쓰면 회전 결과가 '그 점 기준' 공식과 정확히 같아야 한다.
    // (예전엔 엔진이 앞선 편집 적용 후 CoG 로 돌려 Studio 화면과 평행이동만큼 어긋났다 — 2026-10-02 정합성 검토)
    [Fact]
    public void RotateModel_WithPivot_RotatesAboutGivenPoint()
    {
        const double px = 1000.0, py = -2000.0, pz = 500.0;
        var (baseNodes, editedNodes) = RunApplyEdit(new
        {
            axis = "Z",
            angleDeg = 90.0,
            pivot = new { x = px, y = py, z = pz },
        });

        foreach (var (id, p) in editedNodes)
        {
            var b = baseNodes[id];
            // Z 90° (오른손 법칙): (dx, dy) → (-dy, dx)
            p.x.Should().BeApproximately(px - (b.y - py), 1e-3, $"node {id} X");
            p.y.Should().BeApproximately(py + (b.x - px), 1e-3, $"node {id} Y");
            p.z.Should().BeApproximately(b.z, 1e-3, $"node {id} Z");
        }
    }

    private static double Dist((double x, double y, double z) a, (double x, double y, double z) b) =>
        Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));

    private static (Dictionary<int, (double x, double y, double z)> Base, Dictionary<int, (double x, double y, double z)> Edited)
        RunApplyEdit(object rotateParams)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-rotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sampleDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "samples", "hitess_mini", "20260617_082620"));
            File.Copy(Path.Combine(sampleDir, BaseName + ".json"), Path.Combine(tempDir, BaseName + ".json"));
            File.Copy(Path.Combine(sampleDir, BaseName + ".bdf"), Path.Combine(tempDir, BaseName + ".bdf"));

            var baseNodes = ReadNodes(Path.Combine(tempDir, BaseName + ".json"));
            baseNodes.Should().NotBeEmpty();

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
                intents = new[]
                {
                    new
                    {
                        id = Guid.NewGuid().ToString(),
                        kind = "rotateModel",
                        createdAt = DateTimeOffset.UtcNow.ToString("o"),
                        @params = rotateParams,
                        validation = new { status = "ok", warnings = Array.Empty<string>(), errors = Array.Empty<string>() },
                    },
                },
            };
            File.WriteAllText(Path.Combine(tempDir, BaseName + "_edit.json"), JsonSerializer.Serialize(editDocument));

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

            return (baseNodes, ReadNodes(Path.Combine(tempDir, "edited", BaseName + ".json")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static Dictionary<int, (double x, double y, double z)> ReadNodes(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(
            n => n.GetProperty("id").GetInt32(),
            n => (n.GetProperty("x").GetDouble(), n.GetProperty("y").GetDouble(), n.GetProperty("z").GetDouble()));
    }
}
