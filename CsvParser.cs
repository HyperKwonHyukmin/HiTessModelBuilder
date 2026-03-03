using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HiTessModelBuilder.Parsers
{
  public sealed class CsvParser
  {
    private static readonly Regex _numRegex =
      new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex _sizeTypeRegex =
      new(@"^(?<type>[A-Z]+)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public RawCsvDesignData? LastResult { get; private set; }

    // Structure와 Pipe 엔티티를 각각 담을 리스트 (분리 보관)
    public List<StructureEntity> ParsedStruEntities { get; private set; } = new List<StructureEntity>();
    public List<PipeEntity> ParsedPipeEntities { get; private set; } = new List<PipeEntity>();

    public RawCsvDesignData Parse(string? struCsvPath, string? pipeCsvPath)
    {
      ParsedStruEntities.Clear();
      ParsedPipeEntities.Clear();

      if (struCsvPath != null && File.Exists(struCsvPath))
      {
        foreach (var line in File.ReadLines(struCsvPath).Skip(1))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;
          if (!TryStruParseLine(line, out var row)) continue;

          var type = row.Type.Trim().ToUpperInvariant();
          var entity = CreateStruEntity(type);

          entity.Name = row.Name;
          entity.Poss = row.StartPos;
          entity.Pose = row.EndPos;
          entity.Ori = row.Ori;
          entity.SizeDims = row.Dims;
          entity.SizeText = row.SizeRaw;

          entity.ApplyDims(row.Dims);
          ParsedStruEntities.Add(entity);
        }
      }

      if (pipeCsvPath != null && File.Exists(pipeCsvPath))
      {
        foreach (var line in File.ReadLines(pipeCsvPath).Skip(1))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;
          if (!TryPipeParseLine(line, out var row)) continue;

          var type = row.Type.Trim().ToUpperInvariant();
          var entity = new PipeEntity();

          entity.Name = row.Name;
          entity.Type = type;
          entity.Branch = row.Branch;
          entity.APos = row.APos;
          entity.LPos = row.LPos;
          entity.Normal = row.Normal;
          entity.InterPos = row.InterPos;
          entity.P3Pos = row.P3Pos;
          entity.Rest = row.Rest ?? Array.Empty<double>();
          entity.Mass = row.Mass;

          entity.OutDia = row.OutDia;
          entity.Thick = row.Thick;
          entity.OutDia2 = row.OutDia2;
          entity.Thick2 = row.Thick2;

          ParsedPipeEntities.Add(entity);
        }
      }

      var finalResult = GetGroupedData();
      LastResult = finalResult;
      return finalResult;
    }

    private static StructureEntity CreateStruEntity(string typeUpper) => typeUpper switch
    {
      "ANG" => new AngDesignData(),
      "BEAM" => new BeamDesignData(),
      "BSC" => new BscDesignData(),
      "BULB" => new BulbDesignData(),
      "FBAR" => new FbarDesignData(),
      "RBAR" => new RbarDesignData(),
      "TUBE" => new TubeDesignData(),
      _ => new UnknownDesignData(),
    };


    public RawCsvDesignData GetGroupedData()
    {
      // (참고: 향후 RawCsvDesignData 내부에 Pipe 리스트도 포함하도록 업데이트가 필요합니다)
      return new RawCsvDesignData(
          angDesignList: ParsedStruEntities.OfType<AngDesignData>().ToList(),
          beamDesignList: ParsedStruEntities.OfType<BeamDesignData>().ToList(),
          bscDesignList: ParsedStruEntities.OfType<BscDesignData>().ToList(),
          bulbDesignList: ParsedStruEntities.OfType<BulbDesignData>().ToList(),
          fbarDesignList: ParsedStruEntities.OfType<FbarDesignData>().ToList(),
          rbarDesignList: ParsedStruEntities.OfType<RbarDesignData>().ToList(),
          tubeDesignList: ParsedStruEntities.OfType<TubeDesignData>().ToList(),
          unknownDesignList: ParsedStruEntities.OfType<UnknownDesignData>().ToList(),
          // [추가] 파싱 완료된 Pipe 엔티티 리스트 전달
          pipeList: ParsedPipeEntities
      // 여기에 pipeList: ParsedPipeEntities.ToList() 등을 추가하시면 됩니다.
      );
    }

    // -----------------------
    // Parsing Structs
    // -----------------------

    private readonly record struct StruParsedRow(
      string Name, string Type, string SizeRaw, double[] Dims,
      double[] StartPos, double[] EndPos, double[] Ori
    );

    private readonly record struct PipeParsedRow(
      string Name, string Type, string Branch,
      double[] APos, double[] LPos, double[] Normal,
      double[]? InterPos, double[]? P3Pos, double[]? Rest,
      double OutDia, double Thick, double OutDia2, double Thick2, double Mass
    );

    // -----------------------
    // Parsing Logic
    // -----------------------

    private bool TryStruParseLine(string line, out StruParsedRow row)
    {
      row = default;
      try
      {
        var cols = line.Split(',');
        if (cols.Length <= 7) return false;

        string name = cols[0].Trim();
        var (type, dims) = ExtractTypeAndDims(cols[5].Trim());
        var startPos = ExtractDoubles(cols[3]);
        var endPos = ExtractDoubles(cols[4]);
        var ori = ExtractDoubles(cols[7]);

        if (startPos.Length < 3 || endPos.Length < 3 || ori.Length < 3) return false;

        row = new StruParsedRow(name, type, cols[5].Trim(), dims, startPos, endPos, ori);
        return true;
      }
      catch { return false; }
    }

    /// <summary>
    /// 배관(Pipe) 데이터를 파싱합니다. 
    /// 선택적 속성(P3Pos, InterPos)은 값이 없으면 null을 반환하여 유령 노드 생성을 방지합니다.
    /// </summary>
    private bool TryPipeParseLine(string line, out PipeParsedRow row)
    {
      row = default;
      try
      {
        var cols = line.Split(',');
        if (cols.Length < 15) return false;

        string name = cols[0].Trim();
        string type = cols[1].Trim();
        string branch = cols[5].Trim();

        // 1. 필수 좌표 파싱 (없거나 3차원이 아니면 불량 행 처리)
        var aPos = ExtractDoubles(cols[3]);
        var lPos = ExtractDoubles(cols[4]);
        if (aPos.Length < 3 || lPos.Length < 3) return false;

        // 2. 방향 벡터 (없으면 기본값 0,0,0)
        var normal = ExtractDoubles(cols[8]);
        if (normal.Length < 3) normal = new double[] { 0.0, 0.0, 0.0 };

        // 3. 선택적 좌표 (없으면 null 반환)
        var interPos = ExtractDoublesOrNull(cols[9]);
        var p3Pos = ExtractDoublesOrNull(cols[10]);

        // 4. 경계조건 (예: "123456" -> [1,2,3,4,5,6], 없으면 null)
        var rest = ParseRest(cols[13]);

        // 5. 숫자형 데이터 파싱 (없으면 0.0)
        double outDia = ParseDoubleSafe(cols[6]);
        double thick = ParseDoubleSafe(cols[7]);
        double outDia2 = ParseDoubleSafe(cols[11]);
        double thick2 = ParseDoubleSafe(cols[12]);
        double mass = ParseDoubleSafe(cols[14]);

        row = new PipeParsedRow(
            name, type, branch,
            aPos, lPos, normal,
            interPos, p3Pos, rest,
            outDia, thick, outDia2, thick2, mass
        );
        return true;
      }
      catch { return false; }
    }

    // -----------------------
    // Helper Methods
    // -----------------------

    private static (string typeUpper, double[] dims) ExtractTypeAndDims(string sizeText)
    {
      var upper = (sizeText ?? "").Trim().ToUpperInvariant();
      var m = _sizeTypeRegex.Match(upper);
      string type = m.Success ? m.Groups["type"].Value.ToUpperInvariant() : "UNKNOWN";
      var dims = _numRegex.Matches(upper).Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToArray();
      return (type, dims);
    }

    private static double[] ExtractDoubles(string? s)
    {
      return _numRegex.Matches(s ?? "")
                      .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                      .ToArray();
    }

    /// <summary>
    /// 배열 데이터 추출 시, 숫자가 하나도 발견되지 않으면 빈 배열이 아닌 null을 반환합니다.
    /// </summary>
    private static double[]? ExtractDoublesOrNull(string? s)
    {
      var arr = ExtractDoubles(s);
      return arr.Length > 0 ? arr : null;
    }

    private static double ParseDoubleSafe(string? value)
    {
      if (string.IsNullOrWhiteSpace(value)) return 0.0;
      return double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
    }

    /// <summary>
    /// "123456"과 같은 경계조건 문자열을 double 배열 [1, 2, 3, 4, 5, 6]로 파싱합니다.
    /// </summary>
    private static double[]? ParseRest(string? s)
    {
      if (string.IsNullOrWhiteSpace(s)) return null;
      var digits = s.Trim().Where(char.IsDigit).Select(c => (double)char.GetNumericValue(c)).ToArray();
      return digits.Length > 0 ? digits : null;
    }
  }
}
