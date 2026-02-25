using System;
using System.IO;
using System.Text;

namespace HiTessModelBuilder.Services.Logging
{
  /// <summary>
  /// 콘솔 출력과 파일 로깅을 동시에 수행하는 전역 로거입니다.
  /// 설계부 실무자가 파이프라인의 모든 과정과 오류 원인을 추적할 수 있도록 지원합니다.
  /// </summary>
  public class PipelineLogger : IDisposable
  {
    private readonly StreamWriter _fileWriter;
    private readonly string _logFilePath;

    public PipelineLogger(string outputDirectory, string baseFileName)
    {
      // 로그 파일명 예시: 3515-35020-struData_ProcessLog_20231024_153000.txt
      string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
      string logFileName = $"{GetFileNameWithoutWhitespace(baseFileName)}_ProcessLog_{timestamp}.txt";
      _logFilePath = Path.Combine(outputDirectory, logFileName);

      // 파일 스트림 열기 (프로그램 종료 시 닫기 위해 IDisposable 구현)
      _fileWriter = new StreamWriter(_logFilePath, append: false, Encoding.UTF8)
      {
        AutoFlush = true // 즉시 파일에 쓰기
      };

      LogInfo($"[System] 로그 기록 시작: {_logFilePath}");
    }

    /// <summary>
    /// 일반 정보 로그 (콘솔 흰색)
    /// </summary>
    public void LogInfo(string message)
    {
      WriteLog(message, ConsoleColor.White);
    }

    /// <summary>
    /// 성공/통과 로그 (콘솔 녹색/청색)
    /// </summary>
    public void LogSuccess(string message)
    {
      WriteLog(message, ConsoleColor.Cyan);
    }

    /// <summary>
    /// 경고 로그 (콘솔 노란색)
    /// </summary>
    public void LogWarning(string message)
    {
      WriteLog($"[WARNING] {message}", ConsoleColor.Yellow);
    }

    /// <summary>
    /// 치명적 오류 및 Exception 로그 (콘솔 빨간색)
    /// </summary>
    public void LogError(string message, Exception ex = null)
    {
      WriteLog($"[ERROR] {message}", ConsoleColor.Red);
      if (ex != null)
      {
        WriteLog($"   -> Exception Type: {ex.GetType().Name}", ConsoleColor.Red);
        WriteLog($"   -> Message: {ex.Message}", ConsoleColor.Red);
        WriteLog($"   -> Stack Trace: {ex.StackTrace}", ConsoleColor.DarkRed);
      }
    }

    // Action<string> 델리게이트와 호환되도록 제공하는 브릿지 메서드
    public void LogDelegate(string message)
    {
      // 색상 태그 파싱 (기존 코드 호환용)
      if (message.Contains("[실패]") || message.Contains("[경고]")) LogWarning(message);
      else if (message.Contains("[통과]") || message.Contains("[변경]")) LogSuccess(message);
      else LogInfo(message);
    }

    private void WriteLog(string message, ConsoleColor color)
    {
      string timePrefix = $"[{DateTime.Now:HH:mm:ss.fff}] ";
      string fullMessage = timePrefix + message;

      // 1. 파일 쓰기
      _fileWriter.WriteLine(fullMessage);

      // 2. 콘솔 쓰기
      Console.ForegroundColor = color;
      Console.WriteLine(fullMessage);
      Console.ResetColor();
    }

    // 파일명 공백 제거 유틸리티
    private static string GetFileNameWithoutWhitespace(string fileName)
        => Path.GetFileNameWithoutExtension(fileName).Replace(" ", "_");

    public void Dispose()
    {
      LogInfo("[System] 로그 기록 종료.");
      _fileWriter?.Dispose();
    }
  }
}
