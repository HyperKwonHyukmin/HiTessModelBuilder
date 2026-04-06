using System;
using System.IO;
using System.Text;

namespace HiTessModelBuilder.Services.Logging
{
  /// <summary>
  /// Console.Write 및 WriteLine 호출을 가로채어 기존 콘솔(CMD)과 로그 파일 양쪽에 동시에 기록합니다.
  /// 전역에 흩어진 Console 직접 호출을 별도의 리팩토링 없이 100% 파일에 기록하기 위한 Stream 래퍼 클래스입니다.
  /// </summary>
  public class DualTextWriter : TextWriter
  {
    private readonly TextWriter _consoleOut;
    private readonly StreamWriter _fileWriter;

    public DualTextWriter(TextWriter consoleOut, StreamWriter fileWriter)
    {
      _consoleOut = consoleOut;
      _fileWriter = fileWriter;
    }

    // 기존 콘솔의 인코딩을 그대로 따름
    public override Encoding Encoding => _consoleOut.Encoding;

    /// <summary>
    /// 단일 문자 출력을 가로채어 양쪽에 기록합니다.
    /// </summary>
    public override void Write(char value)
    {
      _consoleOut.Write(value);
      _fileWriter.Write(value);
    }

    /// <summary>
    /// 문자열 출력을 가로채어 양쪽에 기록합니다. (WriteLine 호출 시 내부적으로 이 메써드를 통함)
    /// </summary>
    public override void Write(string? value)
    {
      _consoleOut.Write(value);
      _fileWriter.Write(value);
    }

    /// <summary>
    /// 문자 배열 출력을 가로채어 양쪽에 기록합니다.
    /// </summary>
    public override void Write(char[] buffer, int index, int count)
    {
      _consoleOut.Write(buffer, index, count);
      _fileWriter.Write(buffer, index, count);
    }

    /// <summary>
    /// 두 출력 스트림을 모두 플러시합니다.
    /// </summary>
    public override void Flush()
    {
      _consoleOut.Flush();
      _fileWriter.Flush();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
      if (disposing)
        Flush();
      base.Dispose(disposing);
    }
  }
}