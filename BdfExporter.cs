using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Exporter;
using System;
using System.Collections.Generic;
using System.IO;

public static class BdfExporter
{
  /// <summary>
  /// 모델 데이터를 BDF 파일로 출력합니다.
  /// 파일명 규칙: {원본파일명}_{타임스탬프}.bdf
  /// </summary>
  public static void Export(
      FeModelContext context,
      string csvFolderPath,
      string sourceFileName, // stru.csv 또는 pipe.csv의 원본 파일명
      List<int> spcList = null)
  {
    // [수정] maxLoadCaseID 101로 고정하여 빌더 생성
    var bdfBuilder = new BdfBuilder(101, context, spcList);
    bdfBuilder.Run();

    // 1. 타임스탬프 생성
    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 2. 파일명 결정 (확장자 제거 후 접미사 결합)
    string pureFileName = Path.GetFileNameWithoutExtension(sourceFileName);
    string newBdfName = $"{pureFileName}_{timestamp}.bdf";

    string bdfPath = Path.Combine(csvFolderPath, newBdfName);
    File.WriteAllLines(bdfPath, bdfBuilder.BdfLines);

    Console.WriteLine($"[Export] 최종 BDF 파일 생성 완료: {newBdfName}");
  }
}
