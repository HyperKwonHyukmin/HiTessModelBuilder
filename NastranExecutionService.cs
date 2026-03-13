using HiTessModelBuilder.Services.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HiTessModelBuilder.Services.Execution
{
  /// <summary>
  /// MSC/NX Nastran 솔버를 외부 프로세스로 실행하고, 
  /// 결과 파일(.f06)을 분석하여 해석 성공 여부 및 치명적 오류(FATAL)를 검출하는 서비스입니다.
  /// </summary>
  public static class NastranExecutionService
  {
    /// <summary>
    /// BDF 파일을 cmd.exe 환경 변수를 통해 Nastran으로 해석하고 결과를 분석합니다.
    /// </summary>
    /// <param name="bdfFilePath">실행할 BDF 파일의 절대 경로</param>
    /// <param name="log">로깅 델리게이트</param>
    /// <returns>해석 성공(FATAL 없음) 시 true, 실패 시 false 반환</returns>
    public static bool RunAndAnalyze(string bdfFilePath, Action<string> log)
    {
      if (!File.Exists(bdfFilePath))
      {
        log($"[실패] BDF 파일을 찾을 수 없습니다: {bdfFilePath}");
        return false;
      }

      string workDir = Path.GetDirectoryName(bdfFilePath)!;
      string fileName = Path.GetFileName(bdfFilePath);

      log($"\n[Nastran Run] 해석 솔버 구동을 시작합니다. (명령어: nastran {fileName})");

      try
      {
        // 1. Nastran 프로세스 실행 설정 (cmd.exe를 통해 시스템 환경 변수에 등록된 nastran 실행)
        var psi = new ProcessStartInfo
        {
          FileName = "cmd.exe",
          Arguments = $"/c nastran \"{fileName}\" bat=no",
          WorkingDirectory = workDir,
          UseShellExecute = false,
          CreateNoWindow = true
        };

        using (var process = Process.Start(psi))
        {
          if (process != null)
          {
            process.WaitForExit(); // 해석이 끝날 때까지 대기
          }
        }

        log($"[Nastran Run] 프로세스 종료됨. 결과 분석을 시작합니다...");

        // 2. 결과 파일(.f06) 분석
        string f06FilePath = Path.ChangeExtension(bdfFilePath, ".f06");
        return AnalyzeF06File(f06FilePath, log);
      }
      catch (Exception ex)
      {
        log($"[실패] Nastran 프로세스 실행 중 예외 발생: {ex.Message}");
        return false;
      }
    }

    /// <summary>
    /// .f06 파일을 읽어 FATAL MESSAGE 유무를 확인하고 위아래 문맥을 추출합니다.
    /// </summary>
    private static bool AnalyzeF06File(string f06FilePath, Action<string> log)
    {
      if (!File.Exists(f06FilePath))
      {
        log($"[실패] .f06 결과 파일이 생성되지 않았습니다. (해석 실패 또는 환경변수 PATH 문제 의심)");
        return false;
      }

      var lines = File.ReadAllLines(f06FilePath);
      var fatalLineIndices = new List<int>();

      // "FATAL" 키워드 검색
      for (int i = 0; i < lines.Length; i++)
      {
        if (lines[i].Contains("FATAL MESSAGE") || lines[i].Contains("*** FATAL"))
        {
          fatalLineIndices.Add(i);
        }
      }

      if (fatalLineIndices.Count == 0)
      {
        log($"[통과] Nastran 해석 완료! .f06 파일 내 FATAL 오류가 없습니다.");
        return true; // 성공
      }

      // FATAL 에러가 발견된 경우
      log($"[실패] Nastran 해석 실패! .f06 파일에서 {fatalLineIndices.Count}개의 FATAL MESSAGE가 발견되었습니다.");

      int contextRange = 5; // 위아래로 보여줄 줄 수 설정

      foreach (int idx in fatalLineIndices)
      {
        log("\n------------------ [FATAL ERROR CONTEXT] ------------------");
        int startIdx = Math.Max(0, idx - contextRange);
        int endIdx = Math.Min(lines.Length - 1, idx + contextRange);

        for (int j = startIdx; j <= endIdx; j++)
        {
          string prefix = (j == idx) ? ">> " : "   ";
          log($"{prefix} Line {j + 1:D5}: {lines[j]}");
        }
        log("-----------------------------------------------------------\n");
      }

      return false; // 실패
    }
  }
}
