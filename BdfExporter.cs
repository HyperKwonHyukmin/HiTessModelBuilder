using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Exporter;
using System;
using System.Collections.Generic;
using System.IO;

public static class BdfExporter
{
  /// <summary>
  /// 모델 데이터를 BDF 파일로 출력합니다.
  /// </summary>
  public static void Export(
      FeModelContext context,
      string csvFolderPath,
      string fileName,
      List<int> spcList = null,
      bool appendTimestamp = false) // ★ [추가] 타임스탬프 여부 플래그 (기본값 false)
  {
    var bdfBuilder = new BdfBuilder(101, context, spcList);
    bdfBuilder.Run();

    string pureFileName = Path.GetFileNameWithoutExtension(fileName);
    string newBdfName = pureFileName + ".bdf";

    // 옵션이 true일 때만 파일명 뒤에 날짜/시간 정보를 붙임
    if (appendTimestamp)
    {
      string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
      newBdfName = $"{pureFileName}_{timestamp}.bdf";
    }

    string bdfPath = Path.Combine(csvFolderPath, newBdfName);
    File.WriteAllLines(bdfPath, bdfBuilder.BdfLines);

    // 로그 출력을 간결하게 유지하기 위해 파일명만 표시
    Console.WriteLine($"[Export] BDF 추출 완료: {newBdfName}");
  }
}
