using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;

namespace HiTessModelBuilder.Parsers
{
  public class StructureCsvParser
  {
    public List<StructureEntity> ParsedEntities { get; private set; } = new List<StructureEntity>();

    // 좌표 파싱 정규식: "X 61376mm Y -12003mm Z 27477mm" 형태 처리
    private static readonly Regex _coordRegex = new Regex(
        @"X\s*([-\d.]+)\s*mm\s*Y\s*([-\d.]+)\s*mm\s*Z\s*([-\d.]+)\s*mm",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Parse(string filePath)
    {
      if (!File.Exists(filePath))
        throw new FileNotFoundException($"CSV File not found: {filePath}");

      var lines = File.ReadLines(filePath);

      // 헤더(첫 줄) 건너뛰기
      foreach (var line in lines.Skip(1))
      {
        if (string.IsNullOrWhiteSpace(line)) continue;

        try
        {
          var entity = ParseLine(line);
          if (entity != null)
            ParsedEntities.Add(entity);
        }
        catch (Exception ex)
        {
          Console.WriteLine($"[Parsing Error] {ex.Message} in line: {line}");
        }
      }
    }

    private StructureEntity ParseLine(string line)
    {
      // CSV 쉼표 분리
      var cols = line.Split(',');

      // 필수 컬럼 인덱스가 존재하는지 확인 (최소 8개 컬럼 필요)
      if (cols.Length < 8) return null;

      // [데이터 추출] 
      // 이미지 기준: 0:Name, 2:Pos, 3:Poss, 4:Pose, 5:Size, 7:Ori
      string name = cols[0].Trim();
      string posStr = cols[2].Trim();
      string possStr = cols[3].Trim();
      string poseStr = cols[4].Trim();
      string sizeRaw = cols[5].Trim();
      string oriStr = cols[7].Trim();

      // [Factory Logic] Size 문자열 기반 클래스 생성
      StructureEntity entity = CreateEntityBySize(sizeRaw);

      // [데이터 주입]
      entity.Name = name;
      entity.SizeRaw = sizeRaw;
      entity.Pos = ParseCoordinateString(posStr);
      entity.Poss = ParseCoordinateString(possStr);
      entity.Pose = ParseCoordinateString(poseStr);
      entity.Ori = ParseOrientationString(oriStr);

      return entity;
    }

    // Size 문자열 분석하여 적절한 자식 클래스 반환
    private StructureEntity CreateEntityBySize(string sizeRaw)
    {
      string upperSize = sizeRaw.ToUpper();

      if (upperSize.StartsWith("ANG")) return new AngStructure();
      if (upperSize.StartsWith("BEAM")) return new BeamStructure();
      if (upperSize.StartsWith("BSC")) return new BscStructure();
      if (upperSize.StartsWith("BULB")) return new BulbStructure();
      if (upperSize.StartsWith("RBAR")) return new RbarStructure();

      return new UnknownStructure();
    }

    // 좌표 문자열 파싱 ("X ... Y ... Z ...")
    private Point3D ParseCoordinateString(string coordString)
    {
      var match = _coordRegex.Match(coordString);
      if (!match.Success) return new Point3D(0, 0, 0); // 실패 시 원점

      double x = double.Parse(match.Groups[1].Value);
      double y = double.Parse(match.Groups[2].Value);
      double z = double.Parse(match.Groups[3].Value);

      return new Point3D(x, y, z);
    }

    // 방향 벡터 파싱 ("1.000 0.000 0.000" -> 공백 분리)
    private Vector3D ParseOrientationString(string oriString)
    {
      if (string.IsNullOrWhiteSpace(oriString)) return new Vector3D(0, 0, 0);

      // 공백을 기준으로 분리하고 빈 항목 제거
      var parts = oriString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

      if (parts.Length < 3) return new Vector3D(0, 0, 0);

      if (double.TryParse(parts[0], out double x) &&
          double.TryParse(parts[1], out double y) &&
          double.TryParse(parts[2], out double z))
      {
        return new Vector3D(x, y, z);
      }

      return new Vector3D(0, 0, 0);
    }
  }
}
