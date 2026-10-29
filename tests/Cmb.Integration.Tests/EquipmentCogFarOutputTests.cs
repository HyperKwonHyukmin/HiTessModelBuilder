using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// 장비 cog 가 부착점에서 50m 떨어진 CSV(실례 3529_m04 =30137/754429) → EQUIPMENT_COG_FAR 가
// 06_Validation 뿐 아니라 최종 JSON(Studio 가 기본으로 열고 편집하는 'Final')에도 실려야 한다.
// 최종 JSON 진단은 모델 상태로 다시 계산해 단계 경고가 빠지던 것을 막는 회귀 가드.
public class EquipmentCogFarOutputTests
{
    [Fact]
    public void BuildFull_FarCog_IsReportedInValidationAndFinalJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-cogfar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Cmb.Io.Tests", "Fixtures"));
            File.Copy(Path.Combine(fixtureDir, "structure.csv"), Path.Combine(tempDir, "structure.csv"));
            File.Copy(Path.Combine(fixtureDir, "pipe.csv"), Path.Combine(tempDir, "pipe.csv"));
            // 부착점 = 구조 첫 두 부재의 끝점, cog = X 로 50m 밖
            File.WriteAllText(Path.Combine(tempDir, "equip.csv"),
                File.ReadAllText(Path.Combine(fixtureDir, "equip.csv")).TrimEnd() + "\n" +
                " =30137/754429,X 90100mm Y 4908mm Z 39000mm,X 40072mm Y 4908mm Z 39000mm," +
                "X 90072mm Y 4908mm Z 39616mm+X 90225mm Y 4908mm Z 38766mm,715,0+0+0+0+0+0\n");

            var cliDll = Path.Combine(AppContext.BaseDirectory, "Cmb.Cli.dll");
            using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliDll}\" build-full --input \"{tempDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit(60_000).Should().BeTrue();
            process.ExitCode.Should().Be(0);

            var outDir = Directory.GetDirectories(tempDir).Single();
            var finalJson = Directory.GetFiles(outDir, "*.json")
                .Single(p => !Regex.IsMatch(Path.GetFileName(p), @"^\d{2}_"));

            CogFarCodes(Path.Combine(outDir, "06_Validation.json")).Should().ContainSingle();
            var final = CogFarCodes(finalJson);
            final.Should().ContainSingle();
            final[0].Should().Contain("SourceName==30137/754429;").And.Contain("massKg=715");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static List<string> CogFarCodes(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("code").GetString() == "EQUIPMENT_COG_FAR")
            .Select(d => d.GetProperty("message").GetString()!)
            .ToList();
    }
}
