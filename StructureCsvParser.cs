using HiTessModelBuilder.Model.Entities;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HiTessModelBuilder.Parsers
{
  public sealed class StructureCsvParser
  {
    private static readonly Regex _numRegex =
      new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex _sizeTypeRegex =
      new(@"^(?<type>[A-Z]+)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Parse 결과를 밖에서 쓰고 싶으면 프로퍼티로 보관 (선택)
    public RawDesignData? LastResult { get; private set; }

    public RawDesignData Parse(string filePath)
    {
      if (!File.Exists(filePath))
        throw new FileNotFoundException($"CSV File not found: {filePath}");

      // ✅ 여기서 타입별 컨테이너 생성
      var result = new RawDesignData(
        angDesignList: new List<AngDesignData>(),
        beamDesignList: new List<BeamDesignData>(),
        bscDesignList: new List<BscDesignData>(),
        bulbDesignList: new List<BulbDesignData>(),
        rbarDesignList: new List<RbarDesignData>(),
        unknownDesignList: new List<UnknownDesignData>()
      );

      foreach (var line in File.ReadLines(filePath).Skip(1))
      {
        if (string.IsNullOrWhiteSpace(line)) continue;

        if (!TryParseLine(line, out var row))
          continue;

        var type = row.Type.Trim().ToUpperInvariant();

        // ✅ CreateEntity로 생성
        var entity = CreateEntity(type);

        // ✅ 공통 필드 매핑
        entity.Name = row.Name;
        entity.Poss = row.StartPos;
        entity.Pose = row.EndPos;
        entity.Ori = row.Ori;

        // 네 현재 엔티티 구조에서 SizeRaw는 double[]니까 dims 저장
        entity.SizeDims = row.Dims;

        // ✅ 타입별 치수 속성 반영 (각 클래스에서 구현해두면 좋음)
        entity.ApplyDims(row.Dims);

        // ✅ 컨테이너에 분류 저장
        AddToContainer(result, entity, line, type);
      }

      LastResult = result;
      return result;
    }

    private static StructureEntity CreateEntity(string typeUpper) => typeUpper switch
    {
      "ANG" => new AngDesignData(),
      "BEAM" => new BeamDesignData(),
      "BSC" => new BscDesignData(),
      "BULB" => new BulbDesignData(),
      "RBAR" => new RbarDesignData(),
      _ => new UnknownDesignData(),
    };

    private static void AddToContainer(RawDesignData data, StructureEntity e, string rawLine, string typeUpper)
    {
      switch (e)
      {
        case AngDesignData ang:
          data.AngDesignList.Add(ang);
          break;

        case BeamDesignData beam:
          data.BeamDesignList.Add(beam);
          break;

        case BscDesignData bsc:
          data.BscDesignList.Add(bsc);
          break;

        case BulbDesignData bulb:
          data.BulbDesignList.Add(bulb);
          break;

        case RbarDesignData rbar:
          data.RbarDesignList.Add(rbar);
          break;

        case UnknownDesignData unk:
          // Unknown에 타입/라인 보관하고 싶으면 UnknownDesignData에 필드 추가 추천
          // 예: unk.Type = typeUpper; unk.RawLine = rawLine;
          data.UnknownDesignList.Add(unk);
          break;

        default:
          // 혹시 모를 방어 코드
          data.UnknownDesignList.Add(new UnknownDesignData
          {
            Name = e.Name,
            Poss = e.Poss,
            Pose = e.Pose,
            Ori = e.Ori,
            SizeDims = e.SizeDims
          });
          break;
      }
    }

    // -----------------------
    // Parsing helpers
    // -----------------------

    private readonly record struct ParsedRow(
      string Name,
      string Type,
      double[] Dims,
      double[] StartPos,
      double[] EndPos,
      double[] Ori
    );

    private bool TryParseLine(string line, out ParsedRow row)
    {
      row = default;

      try
      {
        var cols = line.Split(',');
        if (cols.Length <= 7) return false;

        string name = cols[0].Trim();
        string possStr = cols[3].Trim();
        string poseStr = cols[4].Trim();
        string sizeText = cols[5].Trim();
        string oriStr = cols[7].Trim();

        var (type, dims) = ExtractTypeAndDims(sizeText);

        var startPos = ExtractDoubles(possStr);
        var endPos = ExtractDoubles(poseStr);
        var ori = ExtractDoubles(oriStr);

        if (startPos.Length < 3 || endPos.Length < 3 || ori.Length < 3)
          return false;

        row = new ParsedRow(name, type, dims, startPos, endPos, ori);
        return true;
      }
      catch
      {
        return false;
      }
    }

    private static (string typeUpper, double[] dims) ExtractTypeAndDims(string sizeText)
    {
      var upper = (sizeText ?? "").Trim().ToUpperInvariant();

      var m = _sizeTypeRegex.Match(upper);
      if (!m.Success) return ("UNKNOWN", Array.Empty<double>());

      var type = m.Groups["type"].Value.ToUpperInvariant();

      var dims = _numRegex.Matches(upper)
                         .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture))
                         .ToArray();

      return (type, dims);
    }

    private static double[] ExtractDoubles(string s)
    {
      return _numRegex.Matches(s ?? "")
                      .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                      .ToArray();
    }
  }
}
