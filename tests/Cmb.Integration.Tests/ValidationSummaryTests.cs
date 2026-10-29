using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// H-4: 최종 검증 결과 요약에 연결성/경계조건 상태를 노출하되, 정상(다중 연결 그룹 가능) 모델의
// passed 는 뒤집지 않아야 한다.
public class ValidationSummaryTests
{
    [Fact]
    public void BuildFull_FinalValidation_PassedStaysTrue_And_SummarySurfacesConnectivityAndBoundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmb-valsummary-" + Guid.NewGuid().ToString("N"));
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

            // 최종 산출물 JSON = 두 자리 phase prefix(^\d\d_) 가 아닌 .json
            var finalJson = Directory.GetFiles(outDir, "*.json")
                .Single(p => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^\d{2}_"));

            using var doc = JsonDocument.Parse(File.ReadAllText(finalJson));
            var vr = doc.RootElement.GetProperty("validationResult");

            // 정상 모델(다중 그룹 가능) → passed 는 true 로 유지된다(회귀 가드).
            vr.GetProperty("passed").GetBoolean().Should().BeTrue();

            // 요약에 연결성/경계조건 상태가 노출된다(추가 정보, passed 에는 영향 없음).
            var summary = vr.GetProperty("summary").GetString()!;
            summary.Should().Contain("연결 그룹");
            summary.Should().Contain("경계조건");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
