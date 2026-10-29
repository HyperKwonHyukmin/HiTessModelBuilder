using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

public class InputAuditOutputTests
{
    [Fact]
    public void BuildFull_WritesCompleteInputAuditJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-input-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Cmb.Io.Tests", "Fixtures"));
            File.Copy(Path.Combine(fixtureDir, "structure.csv"), Path.Combine(tempDir, "structure.csv"));
            File.Copy(Path.Combine(fixtureDir, "pipe.csv"), Path.Combine(tempDir, "pipe.csv"));
            File.Copy(Path.Combine(fixtureDir, "equip.csv"), Path.Combine(tempDir, "equip.csv"));

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            var start = new ProcessStartInfo("dotnet", $"\"{cliDll}\" build-full --input \"{tempDir}\" --stopat SanityPreprocess")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start)!;
            process.WaitForExit(60_000).Should().BeTrue("build-full should finish on the small CSV fixtures");
            process.ExitCode.Should().Be(0);

            var outDir = Directory.GetDirectories(tempDir).Single();
            var auditPath = Path.Combine(outDir, "00_InputAudit.json");
            File.Exists(auditPath).Should().BeTrue();

            using var doc = JsonDocument.Parse(File.ReadAllText(auditPath));
            var root = doc.RootElement;
            var keys = root.GetProperty("_keys");
            var rowAudit = root.GetProperty("rowAudit");
            var expectedRows = File.ReadLines(Path.Combine(tempDir, "structure.csv")).Count()
                             + File.ReadLines(Path.Combine(tempDir, "pipe.csv")).Count()
                             + File.ReadLines(Path.Combine(tempDir, "equip.csv")).Count()
                             - 3;

            keys.GetProperty("rowAudit").GetString().Should().Contain("모든 CSV 데이터 행");
            keys.GetProperty("rowAudit.status").GetString().Should().Contain("converted");
            rowAudit.GetArrayLength().Should().Be(expectedRows);
            root.GetProperty("summary").GetProperty("totalDataRows").GetInt32().Should().Be(expectedRows);
            root.GetProperty("summary").TryGetProperty("createdEntityTotals", out _).Should().BeFalse();
            rowAudit.EnumerateArray().All(HasRequiredAuditFields).Should().BeTrue();
            rowAudit.EnumerateArray().Any(row => row.TryGetProperty("conversionResult", out _)).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildFull_WritesStageSummaryJsonForPhaseOutputs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-stage-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Cmb.Io.Tests", "Fixtures"));
            File.Copy(Path.Combine(fixtureDir, "structure.csv"), Path.Combine(tempDir, "structure.csv"));
            File.Copy(Path.Combine(fixtureDir, "pipe.csv"), Path.Combine(tempDir, "pipe.csv"));
            File.Copy(Path.Combine(fixtureDir, "equip.csv"), Path.Combine(tempDir, "equip.csv"));

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            var start = new ProcessStartInfo("dotnet", $"\"{cliDll}\" build-full --input \"{tempDir}\"")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start)!;
            process.WaitForExit(60_000).Should().BeTrue("build-full should finish on the small CSV fixtures");
            process.ExitCode.Should().Be(0);

            var outDir = Directory.GetDirectories(tempDir).Single();
            var summaryPath = Path.Combine(outDir, "00_StageSummary.json");
            File.Exists(summaryPath).Should().BeTrue();

            using var summaryDoc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = summaryDoc.RootElement;
            var stages = root.GetProperty("stages");

            root.GetProperty("_keys").GetProperty("stages").GetString().Should().Contain("phase");
            root.GetProperty("summary").GetProperty("stageCount").GetInt32().Should().Be(6);
            root.GetProperty("summary").GetProperty("firstStage").GetString().Should().Be("Preprocess");
            root.GetProperty("summary").GetProperty("lastStage").GetString().Should().Be("Validation");
            stages.GetArrayLength().Should().Be(6);

            foreach (var stage in stages.EnumerateArray())
            {
                var jsonFile = stage.GetProperty("jsonFile").GetString()!;
                var bdfFile = stage.GetProperty("bdfFile").GetString()!;
                File.Exists(Path.Combine(outDir, jsonFile)).Should().BeTrue();
                File.Exists(Path.Combine(outDir, bdfFile)).Should().BeTrue();

                using var phaseDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, jsonFile)));
                var phaseRoot = phaseDoc.RootElement;
                stage.GetProperty("counts").GetProperty("nodes").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("nodes").GetArrayLength());
                stage.GetProperty("counts").GetProperty("elements").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("elements").GetArrayLength());
                stage.GetProperty("counts").GetProperty("rigids").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("rigids").GetArrayLength());
                stage.GetProperty("counts").GetProperty("pointMasses").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("pointMasses").GetArrayLength());
                stage.GetProperty("delta").GetProperty("netElementDelta").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("stageDeltas").GetProperty("netElementDelta").GetInt32());
                stage.GetProperty("health").GetProperty("freeEndCount").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("healthMetrics").GetProperty("issues").GetProperty("freeEndNodes").GetInt32());
                stage.GetProperty("connectivity").GetProperty("groupCount").GetInt32()
                    .Should().Be(phaseRoot.GetProperty("connectivity").GetProperty("groupCount").GetInt32());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildFull_WithoutRunNastran_FinalBdfEmitsBottomSpcAndGravity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-no-spc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Cmb.Io.Tests", "Fixtures"));
            File.Copy(Path.Combine(fixtureDir, "structure.csv"), Path.Combine(tempDir, "structure.csv"));
            File.Copy(Path.Combine(fixtureDir, "pipe.csv"), Path.Combine(tempDir, "pipe.csv"));
            File.Copy(Path.Combine(fixtureDir, "equip.csv"), Path.Combine(tempDir, "equip.csv"));

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            var start = new ProcessStartInfo("dotnet", $"\"{cliDll}\" build-full --input \"{tempDir}\"")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start)!;
            process.WaitForExit(60_000).Should().BeTrue("build-full should finish on the small CSV fixtures");
            process.ExitCode.Should().Be(0);

            var outDir = Directory.GetDirectories(tempDir).Single();

            // 최종 산출물 BDF = 두 자리 phase prefix(^\d\d_) 가 아닌 .bdf
            var finalBdfs = Directory.GetFiles(outDir, "*.bdf")
                .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(p), @"^\d{2}_"))
                .ToList();
            finalBdfs.Should().HaveCount(1, "build-full 은 최종 <designName>.bdf 하나를 낸다");

            var bdfText = File.ReadAllText(finalBdfs[0]);

            bdfText.Should().Contain("SPC1");
            bdfText.Should().Contain("GRAV");
            bdfText.Split(Environment.NewLine)
                .Select(l => l.Trim())
                .Should().Contain("SPC = 1");
            bdfText.Split(Environment.NewLine)
                .Select(l => l.Trim())
                .Should().Contain("LOAD = 2");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static bool HasRequiredAuditFields(JsonElement row) =>
        row.TryGetProperty("rawLine", out _) &&
        row.TryGetProperty("rawFields", out _) &&
        row.TryGetProperty("status", out _) &&
        row.TryGetProperty("reason", out _) &&
        !row.TryGetProperty("created", out _);
}
