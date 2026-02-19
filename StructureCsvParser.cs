using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HiTessModelBuilder.Parsers
{
  public sealed class StructureCsvParser
  {
    private static readonly Regex _numRegex =
      new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex _sizeTypeRegex =
      new(@"^(?<type>[A-Z]+)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 파싱 결과를 구조화된 데이터(RawDesignData)로 보관
    /// </summary>
    public RawDesignData? LastResult { get; private set; }

    /// <summary>
    /// [추가됨] CsvRawDataParser 등 외부에서 전체 리스트에 접근하기 위한 속성
    /// </summary>
    public List<StructureEntity> ParsedEntities { get; private set; } = new List<StructureEntity>();

    public RawDesignData Parse(string filePath)
    {
      // 1. 초기화
      ParsedEntities.Clear();

      if (!File.Exists(filePath))
        throw new FileNotFoundException($"CSV File not found: {filePath}");

      // 2. 타입별 컨테이너 생성 (GitHub 버전 로직 유지)
      var result = new RawDesignData(
          angDesignList: new List<AngDesignData>(),
          beamDesignList: new List<BeamDesignData>(),
          bscDesignList: new List<BscDesignData>(),
          bulbDesignList: new List<BulbDesignData>(),
          rbarDesignList: new List<RbarDesignData>(),
          unknownDesignList: new List<UnknownDesignData>()
      );

      // 3. 파일 읽기 및 파싱
      foreach (var line in File.ReadLines(filePath).Skip(1))
      {
        if (string.IsNullOrWhiteSpace(line)) continue;

        // 라인 파싱 시도
        if (!TryParseLine(line, out var row))
          continue;

        var type = row.Type.Trim().ToUpperInvariant();

        // 엔티티 생성
        var entity = CreateEntity(type);

        // 공통 필드 매핑
        entity.Name = row.Name;
        entity.Poss = row.StartPos;
        entity.Pose = row.EndPos;
        entity.Ori = row.Ori;
        entity.SizeDims = row.Dims;
        entity.SizeText = row.SizeRaw; // 원본 사이즈 문자열 저장 (StructureEntity에 SizeText 필드가 있다면)

        // 타입별 치수 속성 반영
        entity.ApplyDims(row.Dims);

        // [중요] 전체 리스트에 추가 (오류 해결 핵심)
        ParsedEntities.Add(entity);

        // 컨테이너에 분류 저장
        AddToContainer(result, entity, line, type);
      }

      LastResult = result;
      return result;
    }


    private static StructureEntity CreateEntity(string typeUpper) => typeUpper switch
    {
      "ANG" => new AngDesignData(),
      "BEAM" => new BeamDesignData(), // 또는 H-BEAM 처리
      "BSC" => new BscDesignData(),
      "BULB" => new BulbDesignData(),
      "RBAR" => new RbarDesignData(),
      _ => new UnknownDesignData(),
    };

    private static void AddToContainer(RawDesignData data, StructureEntity e, string rawLine, string typeUpper)
    {
      switch (e)
      {
        case AngDesignData ang: data.AngDesignList.Add(ang); break;
        case BeamDesignData beam: data.BeamDesignList.Add(beam); break;
        case BscDesignData bsc: data.BscDesignList.Add(bsc); break;
        case BulbDesignData bulb: data.BulbDesignList.Add(bulb); break;
        case RbarDesignData rbar: data.RbarDesignList.Add(rbar); break;
        case UnknownDesignData unk:
          unk.RawLine = rawLine; // UnknownDesignData에 RawLine 필드가 있다고 가정
          data.UnknownDesignList.Add(unk);
          break;
        default:
          // 방어 코드
          data.UnknownDesignList.Add(new UnknownDesignData
          {
            Name = e.Name,
            Poss = e.Poss,
            Pose = e.Pose,
            Ori = e.Ori,
            SizeDims = e.SizeDims,
            RawLine = rawLine
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
      string SizeRaw, // 원본 사이즈 문자열 추가
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
        // 콤마 분리 (따옴표 처리 등이 필요하면 더 정교한 CSV 파서 필요)
        var cols = line.Split(',');

        // 인덱스 안전 점검 (최소 컬럼 수 확인 필요)
        // 예: Name(0), ..., Start(2,3,4), End(5,6,7), Size(?), Ori(?) 
        // 업로드된 코드 기준 인덱스: Name=0, Start=3, End=4, Size=5, Ori=7 
        // (주의: cols[3], cols[4], cols[7]이 하나의 문자열 안에 "x,y,z" 형태로 들어있는지, 
        //  아니면 csv 컬럼 자체가 나뉘어 있는지 확인 필요. 
        //  아래 코드는 GitHub 원본의 로직(ExtractDoubles)을 따름)

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

        // 좌표 데이터 유효성 검사 (각각 3개 이상의 좌표값이 있어야 함)
        if (startPos.Length < 3 || endPos.Length < 3 || ori.Length < 3)
          return false;

        row = new ParsedRow(name, type, sizeText, dims, startPos, endPos, ori);
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

      // 타입 매칭 실패 시 UNKNOWN 처리하되, 치수는 파싱 시도
      string type = m.Success ? m.Groups["type"].Value.ToUpperInvariant() : "UNKNOWN";

      var dims = _numRegex.Matches(upper)
                         .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture))
                         .ToArray();

      return (type, dims);
    }

    private static double[] ExtractDoubles(string s)
    {
      // 문자열 내의 모든 숫자를 추출하여 배열로 반환
      // 예: "100.5, 200, 300" -> [100.5, 200, 300]
      return _numRegex.Matches(s ?? "")
                      .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                      .ToArray();
    }
  }
}
