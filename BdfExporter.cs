using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Exporter;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class BdfExporter
{
  public static void Export(
      FeModelContext context,
      string CsvPath,
      string stageName, List<int> spcList = null)
  {

    // [수정] 계산된 maxLoadCaseID를 생성자에 전달
    var bdfBuilder = new BdfBuilder(101, context, spcList);

    bdfBuilder.Run();
    string newBdfName = stageName + ".bdf";
    string BdfName = Path.Combine(CsvPath, newBdfName);
    File.WriteAllLines(BdfName, bdfBuilder.BdfLines);
  }
}
