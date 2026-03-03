//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Text.RegularExpressions;
//using HiTessModelBuilder.Model.Entities;

//namespace HiTessModelBuilder.Parsers
//{
//  /// <summary>
//  /// Pipe CSV 파일을 읽어 RawPipeDesignData 객체로 변환하는 전담 파서입니다.
//  /// Node나 Element를 직접 생성하지 않고 순수 데이터만 추출합니다.
//  /// </summary>
//  public sealed class PipeCsvParser
//  {
//    private static readonly Regex _pointsRegex = new Regex(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

//    /// <summary>
//    /// 지정된 경로의 CSV 파일을 읽어 배관 엔티티 리스트를 파싱하여 반환합니다.
//    /// </summary>
//    /// <param name="filePath">Pipe CSV 파일의 절대 경로</param>
//    /// <returns>파싱된 배관 데이터 컨테이너 객체</returns>
//    public RawPipeDesignData Parse(string filePath)
//    {
//      var result = new RawPipeDesignData();

//      if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
//      {
//        Console.WriteLine($"[PipeCsvParser] CSV 파일을 찾을 수 없거나 경로가 비어있습니다: {filePath}");
//        return result;
//      }

//      var lines = File.ReadAllLines(filePath);

//      foreach (var line in lines.Skip(1)) // 첫 번째 헤더 행 건너뛰기
//      {
//        if (string.IsNullOrWhiteSpace(line)) continue;

//        var values = line.Split(',');
//        if (values.Length < 15) continue;

//        var entity = new PipeEntity();
//        entity.Type = values[1].Trim();

//        entity.Pos = ExtractDoubles(values[2]);
//        entity.APos = ExtractDoubles(values[3]);
//        entity.LPos = ExtractDoubles(values[4]);

//        if (double.TryParse(values[6], out double od)) entity.OutDia = od;
//        if (double.TryParse(values[7], out double th)) entity.Thick = th;

//        // Normal 벡터 파싱
//        entity.Normal = string.IsNullOrWhiteSpace(values[8])
//            ? new double[] { 0.0, 0.0, 0.0 }
//            : values[8].Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();

//        // P3Pos 파싱 (TEE 타입 등 분기점)
//        if (!string.IsNullOrWhiteSpace(values[10]))
//          entity.P3Pos = ExtractDoubles(values[10]);

//        if (double.TryParse(values[11], out double od2)) entity.OutDia2 = od2;
//        if (double.TryParse(values[12], out double th2)) entity.Thick2 = th2;
//        if (double.TryParse(values[14], out double mass)) entity.Mass = mass;

//        result.PipeList.Add(entity);
//      }

//      return result;
//    }

//    /// <summary>
//    /// 정규식을 이용하여 문자열 내부의 모든 숫자를 double 배열로 추출합니다.
//    /// </summary>
//    private static double[] ExtractDoubles(string input)
//    {
//      return _pointsRegex.Matches(input ?? "")
//                         .Cast<Match>()
//                         .Select(m => double.Parse(m.Value))
//                         .ToArray();
//    }
//  }
//}