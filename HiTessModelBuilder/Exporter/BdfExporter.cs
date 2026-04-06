using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Exporter;
using System;
using System.Collections.Generic;
using System.IO;

public static class BdfExporter
{
  /// <summary>
  /// 모델 데이터를 BDF 파일로 출력합니다.
  /// 넘겨받은 outputFileName 문자열 그대로 파일을 생성합니다.
  /// </summary>
  public static void Export(
      FeModelContext context,
      string csvFolderPath,
      string outputFileName, // [수정] 외부에서 완성된 파일명을 직접 주입받음
      List<int> spcList = null)
  {
    var bdfBuilder = new BdfBuilder(101, context, spcList);
    bdfBuilder.Run();

    string bdfPath = Path.Combine(csvFolderPath, outputFileName);
    File.WriteAllLines(bdfPath, bdfBuilder.BdfLines);

    Console.WriteLine($"[Export] BDF 추출 완료: {outputFileName}");
  }
}