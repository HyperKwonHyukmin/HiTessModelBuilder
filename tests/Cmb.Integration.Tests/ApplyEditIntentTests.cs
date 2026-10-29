using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

public class ApplyEditIntentTests
{
    [Fact]
    public void EmptyPipeFluid_UpdatesEditedJsonAndBdfMaterialDensity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-empty-fluid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sampleDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "samples", "hitess_mini", "20260617_082620"));
            const string baseName = "06_Validation";
            File.Copy(Path.Combine(sampleDir, baseName + ".json"), Path.Combine(tempDir, baseName + ".json"));
            File.Copy(Path.Combine(sampleDir, baseName + ".bdf"), Path.Combine(tempDir, baseName + ".bdf"));

            using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, baseName + ".json")));
            var meta = stage.RootElement.GetProperty("meta");
            var pipeMaterialIds = FindPipeMaterialIds(stage.RootElement);
            pipeMaterialIds.Should().NotBeEmpty();

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
                        kind = "emptyPipeFluid",
                        createdAt = DateTimeOffset.UtcNow.ToString("o"),
                        @params = new { materialIds = pipeMaterialIds, targetRho = 7.85e-9 },
                        validation = new { status = "ok", warnings = Array.Empty<string>(), errors = Array.Empty<string>() },
                    },
                },
            };
            File.WriteAllText(
                Path.Combine(tempDir, baseName + "_edit.json"),
                JsonSerializer.Serialize(editDocument));

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

            var editedJsonPath = Path.Combine(tempDir, "edited", baseName + ".json");
            using var edited = JsonDocument.Parse(File.ReadAllText(editedJsonPath));
            var editedMaterials = edited.RootElement.GetProperty("materials")
                .EnumerateArray()
                .ToDictionary(m => m.GetProperty("id").GetInt32());
            foreach (var materialId in pipeMaterialIds)
                editedMaterials[materialId].GetProperty("rho").GetDouble().Should().BeApproximately(7.85e-9, 1e-15);

            var editedBdf = File.ReadAllText(Path.Combine(tempDir, "edited", baseName + ".bdf"));
            foreach (var materialId in pipeMaterialIds)
            {
                var mat1Line = editedBdf.Split(Environment.NewLine)
                    .Single(line => line.StartsWith("MAT1") && ParseSmallFieldInt(line, 1) == materialId);
                mat1Line.Should().Contain("7.85-9");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int[] FindPipeMaterialIds(JsonElement root)
    {
        var materialByProperty = root.GetProperty("properties")
            .EnumerateArray()
            .ToDictionary(
                property => property.GetProperty("id").GetInt32(),
                property => property.GetProperty("materialId").GetInt32());

        return root.GetProperty("elements")
            .EnumerateArray()
            .Where(element => element.GetProperty("category").GetString() == "Pipe")
            .Select(element => materialByProperty[element.GetProperty("propertyId").GetInt32()])
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    private static int ParseSmallFieldInt(string line, int fieldIndex) =>
        int.Parse(line.Substring(fieldIndex * 8, 8).Trim());
}
