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

    // Structure�� Pipe ��ƼƼ�� ���� ���� ����Ʈ (�и� ����)
    public List<StructureEntity> ParsedStruEntities { get; private set; } = new List<StructureEntity>();
    public List<PipeEntity> ParsedPipeEntities { get; private set; } = new List<PipeEntity>();
    public List<EquipEntity> ParsedEquipEntities { get; private set; } = new List<EquipEntity>();

    public RawCsvDesignData Parse(string? struCsvPath, string? pipeCsvPath, string? equipCsvPath)
    {
      ParsedStruEntities.Clear();
      ParsedPipeEntities.Clear();

      // strucCsv �Ľ�
      if (struCsvPath != null && File.Exists(struCsvPath))
      {
        foreach (var line in File.ReadLines(struCsvPath).Skip(1))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;

          // �� Name�� ���� �����Ͽ� �Ľ� ���� �� �α׿� ���
          string rawName = line.Split(',').FirstOrDefault()?.Trim() ?? "Unknown";

          if (!TryStruParseLine(line, out var row))
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[�Ľ� ����] ������ ������ �� ������ ���ܵ�: '{rawName}'");
            Console.ResetColor();
            continue;
          }

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
          entity.Weld = row.Weld;
        }
      }

      // pipeCsv �Ľ�
      if (pipeCsvPath != null && File.Exists(pipeCsvPath))
      {
        foreach (var line in File.ReadLines(pipeCsvPath).Skip(1))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;

          string rawName = line.Split(',').FirstOrDefault()?.Trim() ?? "Unknown";

          if (!TryPipeParseLine(line, out var row))
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[�Ľ� ����] ������ ������ �� ������ ���ܵ�: '{rawName}'");
            Console.ResetColor();
            continue;
          }

          var type = row.Type.Trim().ToUpperInvariant();
          var entity = new PipeEntity();

          entity.Name = row.Name;
          entity.Type = type;
          entity.Branch = row.Branch;
          entity.Pos = row.Pos;
          entity.APos = row.APos;
          entity.LPos = row.LPos;
          entity.Normal = row.Normal;
          entity.InterPos = row.InterPos;
          entity.P3Pos = row.P3Pos;
          entity.Rest = row.Rest;
          entity.Mass = row.Mass;
          entity.Remark = row.Remark;

          entity.OutDia = row.OutDia;
          entity.Thick = row.Thick;
          entity.OutDia2 = row.OutDia2;
          entity.Thick2 = row.Thick2;

          ParsedPipeEntities.Add(entity);
        }
      }

      // equiCsv �Ľ�   
      if (equipCsvPath != null && File.Exists(equipCsvPath))
      {
        foreach (var line in File.ReadLines(equipCsvPath).Skip(1))
        {
          if (string.IsNullOrWhiteSpace(line)) continue;

          string rawName = line.Split(',').FirstOrDefault()?.Trim() ?? "Unknown";
          if (!TryEquipParseLine(line, out var row))
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[�Ľ� ����] ������ ������ �� ������ ���ܵ�: '{rawName}'");
            Console.ResetColor();
            continue;
          }

          // [�ٽ�] ���� ���� �ڵ��� ���� 0 ��ŵ ���� �ݿ�
          if (row.Mass == 0.0 && row.Wvol == 0.0) Console.WriteLine($"[�Ľ� ����] ��� �߷� 0���� ���ܵ�: '{row.Name}'"); ;

          var entity = new EquipEntity
          {
            Name = row.Name,
            Pos = row.Pos,
            Cog = row.Cog,
            InterPos = row.InterPos,
            Mass = row.Mass,
            Wvol = row.Wvol
          };

          ParsedEquipEntities.Add(entity);
        }
      }

      var finalResult = GetGroupedData();
      LastResult = finalResult;
      return finalResult;
    }

    // equiCsv �Ľ�
    


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
      // (����: ���� RawCsvDesignData ���ο� Pipe ����Ʈ�� �����ϵ��� ������Ʈ�� �ʿ��մϴ�)
      return new RawCsvDesignData(
          angDesignList: ParsedStruEntities.OfType<AngDesignData>().ToList(),
          beamDesignList: ParsedStruEntities.OfType<BeamDesignData>().ToList(),
          bscDesignList: ParsedStruEntities.OfType<BscDesignData>().ToList(),
          bulbDesignList: ParsedStruEntities.OfType<BulbDesignData>().ToList(),
          fbarDesignList: ParsedStruEntities.OfType<FbarDesignData>().ToList(),
          rbarDesignList: ParsedStruEntities.OfType<RbarDesignData>().ToList(),
          tubeDesignList: ParsedStruEntities.OfType<TubeDesignData>().ToList(),
          unknownDesignList: ParsedStruEntities.OfType<UnknownDesignData>().ToList(),
          pipeList: ParsedPipeEntities, equipList: ParsedEquipEntities);
    }

    // -----------------------
    // Parsing Structs
    // -----------------------

    private readonly record struct StruParsedRow(
      string Name, string Type, string SizeRaw, double[] Dims,
      double[] StartPos, double[] EndPos, double[] Ori, string Weld
    );

    private readonly record struct PipeParsedRow(
      string Name, string Type, string Branch, double[] Pos,
      double[] APos, double[] LPos, double[] Normal,
      double[]? InterPos, double[]? P3Pos, string Rest,
      double OutDia, double Thick, double OutDia2, double Thick2, string Mass, string Remark
    );

    private readonly record struct EquipParsedRow(
      string Name, double[] Pos, double[] Cog, double[] InterPos, double Mass, double Wvol);

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
        // [�ű� �߰�] 10��° ��(�ε��� 9)�� �����ϸ� �Ľ�, ������ �� ���ڿ�
        string weld = cols.Length > 9 ? cols[9].Trim().ToLowerInvariant() : "";

        if (startPos.Length < 3 || endPos.Length < 3 || ori.Length < 3) return false;

        row = new StruParsedRow(name, type, cols[5].Trim(), dims, startPos, endPos, ori, weld);
        return true;
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[경고] 구조 CSV 줄 파싱 중 오류 발생: {ex.GetType().Name} - {ex.Message}");
        Console.ResetColor();
        return false;
      }
    }

    /// <summary>
    /// ���(Pipe) �����͸� �Ľ��մϴ�.
    /// ������ �Ӽ�(P3Pos, InterPos)�� ���� ������ null�� ��ȯ�Ͽ� ���� ��� ������ �����մϴ�.
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

        // 1. �ʼ� ��ǥ �Ľ� (���ų� 3������ �ƴϸ� �ҷ� �� ó��)
        var Pos = ExtractDoubles(cols[2]);
        var aPos = ExtractDoubles(cols[3]);
        var lPos = ExtractDoubles(cols[4]);
        if (Pos.Length < 3 || aPos.Length < 3 || lPos.Length < 3) return false;

        // 2. ���� ���� (������ �⺻�� 0,0,0)
        var normal = ExtractDoubles(cols[8]);
        if (normal.Length < 3) normal = new double[] { 0.0, 0.0, 0.0 };

        // 3. ������ ��ǥ (������ null ��ȯ)
        var interPos = ExtractDoublesOrNull(cols[9]);
        var p3Pos = ExtractDoublesOrNull(cols[10]);

        // 4. ������� (��: "123456" -> [1,2,3,4,5,6], ������ null)
        var rest = string.IsNullOrWhiteSpace(cols[13]) ? null : cols[13].Trim();

        // 5. ������ ������ �Ľ� (������ 0.0)
        double outDia = ParseDoubleSafe(cols[6]);
        double thick = ParseDoubleSafe(cols[7]);
        double outDia2 = ParseDoubleSafe(cols[11]);
        double thick2 = ParseDoubleSafe(cols[12]);
        string mass = string.IsNullOrWhiteSpace(cols[14]) ? null : cols[14].Trim();
        string remark = string.IsNullOrWhiteSpace(cols[16]) ? null : cols[16].Trim();

        row = new PipeParsedRow(
            name, type, branch, Pos,
            aPos, lPos, normal,
            interPos, p3Pos, rest,
            outDia, thick, outDia2, thick2, mass, remark
        );
        return true;
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[경고] 배관 CSV 줄 파싱 중 오류 발생: {ex.GetType().Name} - {ex.Message}");
        Console.ResetColor();
        return false;
      }
    }
    private bool TryEquipParseLine(string line, out EquipParsedRow row)
    {
      row = default;
      try
      {
        var cols = line.Split(',');
        if (cols.Length < 6) return false;

        string name = cols[0].Trim();

        // ExtractDoubles�� '+' ��ȣ�� ������ ���ԽĿ� ���� ����� �ν��Ͽ� 
        // 1���� ���� �迭([x1,y1,z1, x2,y2,z2...])�� �Ϻ��ϰ� ���ݴϴ�.
        var pos = ExtractDoubles(cols[1]);
        var cog = ExtractDoubles(cols[2]);
        var interPos = ExtractDoubles(cols[3]);

        double mass = ParseDoubleSafe(cols[4]);
        double wvol = ParseDoubleSafe(cols[5]);

        row = new EquipParsedRow(name, pos, cog, interPos, mass, wvol);
        return true;
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[경고] 장비 CSV 줄 파싱 중 오류 발생: {ex.GetType().Name} - {ex.Message}");
        Console.ResetColor();
        return false;
      }
    }

    //private void TryEquipParseLine(string line, out EquipParsedRow row)
    //{

    //}

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
    /// �迭 ������ ���� ��, ���ڰ� �ϳ��� �߰ߵ��� ������ �� �迭�� �ƴ� null�� ��ȯ�մϴ�.
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
    /// "123456"�� ���� ������� ���ڿ��� double �迭 [1, 2, 3, 4, 5, 6]�� �Ľ��մϴ�.
    /// </summary>
    private static double[]? ParseRest(string? s)
    {
      if (string.IsNullOrWhiteSpace(s)) return null;
      var digits = s.Trim().Where(char.IsDigit).Select(c => (double)char.GetNumericValue(c)).ToArray();
      return digits.Length > 0 ? digits : null;
    }
  }



}


