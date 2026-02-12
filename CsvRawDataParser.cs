using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Parsers
{
  public class CsvRawDataParser
  {
    private readonly string _strucCsv;
    private readonly string _pipeCsv_;
    private readonly string _equipCsv;
    bool _debugPrint;

    public CsvRawDataParser(string StrucCsv, string PipeCsv, string EquipCsv, bool debugPrint = false)
    {
      _strucCsv = StrucCsv;
      _pipeCsv_ = PipeCsv;
      _equipCsv = EquipCsv;
      _debugPrint = debugPrint;
    }

    public void Run()
    {
      var structureParser = new StructureCsvParser();
      structureParser.Parse(_strucCsv);
    }

  }
}
