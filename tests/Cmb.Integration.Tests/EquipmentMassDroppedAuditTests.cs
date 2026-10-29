using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// 장비 질량이 있는데 부착 노드를 못 찾으면 엔진이 질량을 뺀다(실례 3529_m04 =21945/1865951 8.2 kg).
// 예전엔 입력 감사에 'converted' 로 남고 trace 에만 기록돼 WorkBench 판정('질량 있는 행 제외')이 못 잡았다.
// 이제 입력 감사가 그 행을 error / equipment_mass_not_attached 로 남겨야 한다.
public class EquipmentMassDroppedAuditTests
{
    [Fact]
    public void BuildFull_EquipmentWithoutAttachNode_IsAuditedAsMassNotAttached()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-eqdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Cmb.Io.Tests", "Fixtures"));
            File.Copy(Path.Combine(fixtureDir, "structure.csv"), Path.Combine(tempDir, "structure.csv"));
            File.Copy(Path.Combine(fixtureDir, "pipe.csv"), Path.Combine(tempDir, "pipe.csv"));
            // 부착점(interPos) 없음 + cog 가 어떤 부재 노드에서도 10 mm 밖 → 질량 누락
            File.WriteAllText(Path.Combine(tempDir, "equip.csv"),
                File.ReadAllText(Path.Combine(fixtureDir, "equip.csv")).TrimEnd() + "\n" +
                " =21945/1865951,X 1000mm Y 1000mm Z 1000mm,X 1000mm Y 1000mm Z 1000mm,,8.2,0+0+0+0+0+0\n");

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliDll}\" build-full --input \"{tempDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit(60_000).Should().BeTrue();
            process.ExitCode.Should().Be(0);

            var outDir = Directory.GetDirectories(tempDir).Single();
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "00_InputAudit.json")));
            var row = doc.RootElement.GetProperty("rowAudit").EnumerateArray()
                .Single(r => r.GetProperty("name").GetString()!.Trim() == "=21945/1865951");
            row.GetProperty("status").GetString().Should().Be("error");
            row.GetProperty("reasonCode").GetString().Should().Be("equipment_mass_not_attached");
            row.GetProperty("reason").GetString().Should().Contain("8.2").And.Contain("부착 노드");

            var summary = doc.RootElement.GetProperty("summary");
            // 픽스처 첫 장비 행(=38329/60941, 1 kg, 부착점 없음)도 실제로 질량이 빠지는 행이라 함께 집계된다.
            summary.GetProperty("ignoredByReason").GetProperty("equipment_mass_not_attached").GetInt32().Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
