using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Parsers
{
  public class StructureCsvParser
  {
    public void Parse(string filePath)
    {
      if (!File.Exists(filePath))
        throw new FileNotFoundException($"Structure CSV 파일을 찾을 수 없습니다: {filePath}");

      var lines = File.ReadAllLines(filePath);

      foreach (var line in lines)
      {
        Console.WriteLine(line);
      }
    }
  }
}
