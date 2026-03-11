using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Services.Builders
{
  public class PipeModelBuilder
  {
    private readonly FeModelContext _context;
    private readonly Dictionary<string, List<int>> _pipeElementIDsByType;
    private readonly bool _useFluidDensity;
    private readonly bool _debugPrint;

    // [변경됨] 생성자에 useFluidDensity 옵션 추가
    public PipeModelBuilder(FeModelContext context, Dictionary<string, List<int>> pipeElementIDsByType, bool useFluidDensity = true, bool debugPrint = false)
    {
      _context = context ?? throw new ArgumentNullException(nameof(context));
      _pipeElementIDsByType = pipeElementIDsByType ?? throw new ArgumentNullException(nameof(pipeElementIDsByType));
      _useFluidDensity = useFluidDensity;
      _debugPrint = debugPrint;
    }

    public void Build(List<PipeEntity> pipeList)
    {
      foreach (var pipeData in pipeList)
      {
        bool isMassValid = double.TryParse(pipeData.Mass, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double massValue);

        // [제거됨] 공통 Material(Steel) 일괄 생성 코드 삭제 (아래 헬퍼 메써드로 위임)

        var extraData = new Dictionary<string, string?>
        {
            { "Name", pipeData.Name },
            { "Branch", pipeData.Branch },
            { "Rest", pipeData.Rest },
            { "Mass", pipeData.Mass },
            { "Classification", "Pipe" },
            { "Type", pipeData.Type },
            { "Remark", pipeData.Remark }
        };

        // 타입별 분기 처리 (Strategy)
        // [변경됨] 각 하위 메서드에서 Material을 직접 찾도록 시그니처 수정
        switch (pipeData.Type)
        {
          case "TUBI":
          case "ELBO":
          case "BEND":
            BuildMultiSegmentPipe(pipeData, extraData);
            break;
          case "OLET":
          case "REDU":
          case "COUP":
            BuildSingleSegmentPipe(pipeData, extraData);
            break;
          case "TEE":
            BuildTeePipe(pipeData, extraData);
            break;
          case "FLAN":
            BuildFlange(pipeData, extraData, isMassValid, massValue);
            break;
          case "VALV":
          case "TRAP":
          case "FILT":
          case "EXP":
            BuildInlineEquipment(pipeData, extraData, isMassValid, massValue);
            break;
          case "VTWA":
            BuildVtwa(pipeData, extraData, isMassValid, massValue);
            break;
          case "UBOLT":
            BuildUBolt(pipeData, extraData);
            break;
          case "ATTA":
            BuildAttachment(pipeData, extraData, isMassValid, massValue);
            break;
          default:
            // ★ [사각지대 4] 알 수 없는 배관 누락 로그 가
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[생성 누락] 알 수 없는 배관 타입({pipeData.Type})으로 생성이 취소되었습니다. Name: '{pipeData.Name}'");
            Console.ResetColor();
            break;
        }
      }
    }

    /// <summary>
    /// [신규 추가] 배관의 치수(외경, 내경)를 기반으로 내부 유체의 질량을 
    /// 강철의 밀도로 환산한 '등가 밀도(Equivalent Density)' Material을 생성하거나 반환합니다.
    /// </summary>
    private int GetOrCreatePipeMaterial(double rOut, double rIn)
    {
      double steelDensity = 7.85e-09; // 강철 밀도 (ton/mm^3)
      double waterDensity = 1.0e-09;  // 물 밀도 (ton/mm^3)

      double finalDensity = steelDensity;
      string matName = "Steel";

      // 유체 밀도 보정 로직 (외경이 내경보다 크고, 내경이 0보다 클 때만 성립)
      if (_useFluidDensity && rOut > rIn && rIn > 0)
      {
        double steelArea = (rOut * rOut) - (rIn * rIn); // pi 생략 (약분됨)
        double fluidArea = (rIn * rIn);                 // pi 생략 (약분됨)

        // 등가 밀도 산출 공식
        finalDensity = ((steelArea * steelDensity) + (fluidArea * waterDensity)) / steelArea;
        matName = $"Steel_Fluid_R{rOut:F1}x{rIn:F1}"; // 치수별 고유 Material 이름 부여
      }

      return _context.Materials.AddOrGet(matName, 206000, 0.3, finalDensity);
    }

    private void BuildMultiSegmentPipe(PipeEntity pipeData, Dictionary<string, string?> extraData)
    {
      int materialID = GetOrCreatePipeMaterial(pipeData.Dim1, pipeData.Dim2); // [신규]
      double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
      int propertyID = _context.Properties.AddOrGet("TUBE", propertyDim, materialID);

      List<int> nodeChain = new List<int>();
      nodeChain.Add(_context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]));

      if (pipeData.InterPos != null && pipeData.InterPos.Length >= 3)
      {
        for (int i = 0; i < pipeData.InterPos.Length; i += 3)
          nodeChain.Add(_context.Nodes.AddOrGet(pipeData.InterPos[i], pipeData.InterPos[i + 1], pipeData.InterPos[i + 2]));
      }
      nodeChain.Add(_context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]));

      for (int i = 0; i < nodeChain.Count - 1; i++)
        CreateElementSafe(nodeChain[i], nodeChain[i + 1], propertyID, pipeData.Normal, extraData, pipeData);
    }

    private void BuildSingleSegmentPipe(PipeEntity pipeData, Dictionary<string, string?> extraData)
    {
      int materialID = GetOrCreatePipeMaterial(pipeData.Dim1, pipeData.Dim2); // [신규]
      double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
      int propertyID = _context.Properties.AddOrGet("TUBE", propertyDim, materialID);
      double[] barOrientation = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);

      int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
      int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

      CreateElementSafe(startNode, endNode, propertyID, barOrientation, extraData, pipeData);
    }

    private void BuildTeePipe(PipeEntity pipeData, Dictionary<string, string?> extraData)
    {
      // [핵심] TEE는 메인관과 분기관의 두께가 다를 수 있으므로 재질(등가밀도)도 각각 생성
      int matIdMain = GetOrCreatePipeMaterial(pipeData.Dim1, pipeData.Dim2);
      int matIdBranch = GetOrCreatePipeMaterial(pipeData.Dim3, pipeData.Dim4);

      int propertyID1 = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim1, pipeData.Dim2 }, matIdMain);
      int propertyID2 = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim3, pipeData.Dim4 }, matIdBranch);

      int centerNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
      int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
      int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

      CreateElementSafe(startNode, centerNode, propertyID1, pipeData.Normal, extraData, pipeData);
      CreateElementSafe(centerNode, endNode, propertyID1, pipeData.Normal, extraData, pipeData);

      if (pipeData.P3Pos != null && pipeData.P3Pos.Length >= 3)
      {
        int p3Node = _context.Nodes.AddOrGet(pipeData.P3Pos[0], pipeData.P3Pos[1], pipeData.P3Pos[2]);
        CreateElementSafe(centerNode, p3Node, propertyID2, pipeData.Normal, extraData, pipeData);
      }
    }

    private void BuildFlange(PipeEntity pipeData, Dictionary<string, string?> extraData, bool isMassValid, double massValue)
    {
      if (isMassValid && massValue > 0.0)
      {
        int posNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
        _context.PointMasses.AddNew(posNode, massValue, extraData);
      }

      if (pipeData.OutDia > 0)
      {
        int materialID = GetOrCreatePipeMaterial(pipeData.Dim1, pipeData.Dim2); // [신규]
        int propertyID = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim1, pipeData.Dim2 }, materialID);
        double[] barOrientation = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);
        int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
        int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

        CreateElementSafe(startNode, endNode, propertyID, barOrientation, extraData, pipeData);
      }
    }

    // ... (이하 BuildInlineEquipment, BuildVtwa, BuildUBolt, BuildAttachment, CreateElementSafe는 기존과 동일) ...
    private void BuildInlineEquipment(PipeEntity pipeData, Dictionary<string, string?> extraData, bool isMassValid, double massValue)
    {
      int centerNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
      int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
      int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

      if (startNode != endNode)
      {
        int rbeId = _context.Rigids.AddNew(centerNode, new List<int> { startNode, endNode }, "123456", extraData);
        if (_pipeElementIDsByType.ContainsKey(pipeData.Type)) _pipeElementIDsByType[pipeData.Type].Add(rbeId);
      }
      else
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[생성 누락] 시작점과 끝점이 같아 밸브/장비(RBE) 생성이 취소되었습니다. Name: '{pipeData.Name}'");
        Console.ResetColor();
      }

      if (isMassValid && massValue > 0.0)
      {
        _context.PointMasses.AddNew(centerNode, massValue, extraData);
      }
    }

    private void BuildVtwa(PipeEntity pipeData, Dictionary<string, string?> extraData, bool isMassValid, double massValue)
    {
      int centerNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
      List<int> depNodes = new List<int>
      {
          _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]),
          _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2])
      };

      if (pipeData.P3Pos != null && pipeData.P3Pos.Length >= 3)
      {
        depNodes.Add(_context.Nodes.AddOrGet(pipeData.P3Pos[0], pipeData.P3Pos[1], pipeData.P3Pos[2]));
      }

      int rbeId = _context.Rigids.AddNew(centerNode, depNodes, "123456", extraData);
      if (_pipeElementIDsByType.ContainsKey(pipeData.Type)) _pipeElementIDsByType[pipeData.Type].Add(rbeId);

      if (isMassValid && massValue > 0.0)
      {
        _context.PointMasses.AddNew(centerNode, massValue, extraData);
      }
    }

    private void BuildUBolt(PipeEntity pipeData, Dictionary<string, string?> extraData)
    {
      int indepNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
      string restStr = string.IsNullOrWhiteSpace(pipeData.Rest) ? "123456" : pipeData.Rest;
      extraData["Remark"] = pipeData.Remark;

      int rbeId = _context.Rigids.AddOrGet(indepNode, Array.Empty<int>(), restStr, extraData);

      if (_pipeElementIDsByType.ContainsKey(pipeData.Type))
        _pipeElementIDsByType[pipeData.Type].Add(rbeId);
    }

    private void BuildAttachment(PipeEntity pipeData, Dictionary<string, string?> extraData, bool isMassValid, double massValue)
    {
      if (isMassValid && massValue > 10.0)
      {
        var validPipeNodes = _context.GetNodesUsedInPipeElements();
        var targetPos = new Point3D(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);

        int closestPipeNodeId = _context.Nodes.FindClosestValidNode(targetPos, validPipeNodes, tolerance: 100.0);

        if (closestPipeNodeId != -1)
        {
          _context.PointMasses.AddNew(closestPipeNodeId, massValue, extraData);
        }
        else if (_debugPrint)
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.WriteLine($"[Warning] 100mm 이내 부착할 배관 노드 없음. ATTA 생략: {pipeData.Name}");
          Console.ResetColor();
        }
      }
    }

    private void CreateElementSafe(int n1, int n2, int propId, double[] ori, Dictionary<string, string?> extra, PipeEntity pipe)
    {
      if (n1 == n2)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[생성 누락] 시작점과 끝점이 같아(길이 0) 배관 요소 생성이 취소되었습니다. Name: '{pipe.Name}'");
        Console.ResetColor();
        return;
      }
      try
      {
        var p1 = _context.Nodes[n1];
        var p2 = _context.Nodes[n2];

        double[] calcOri = GeometryUtils.CalculateBarOrientation(
            new double[] { p1.X, p1.Y, p1.Z },
            new double[] { p2.X, p2.Y, p2.Z }
        );

        var elementExtra = extra.ToDictionary(k => k.Key, v => v.Value ?? "");
        elementExtra["OriX"] = calcOri[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        elementExtra["OriY"] = calcOri[1].ToString(System.Globalization.CultureInfo.InvariantCulture);
        elementExtra["OriZ"] = calcOri[2].ToString(System.Globalization.CultureInfo.InvariantCulture);

        int eid = _context.Elements.AddNew(new List<int> { n1, n2 }, propId, calcOri, elementExtra);

        if (_pipeElementIDsByType.ContainsKey(pipe.Type))
          _pipeElementIDsByType[pipe.Type].Add(eid);
      }
      catch (Exception ex)
      {

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Error] {pipe.Type} 생성 실패! Name: {pipe.Name} / 에러: {ex.Message}");
        Console.ResetColor();
        
      }
    }
  }
}
