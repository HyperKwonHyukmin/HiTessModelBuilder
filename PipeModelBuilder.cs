using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;

namespace HiTessModelBuilder.Services.Builders
{
  /// <summary>
  /// 배관(Pipe) 데이터를 기반으로 FE 모델(Nodes, Elements, Rigids, PointMasses)을 생성하는 전담 빌더 클래스입니다.
  /// 기존 RawFeModelBuilder의 복잡도를 낮추고 배관 생성 로직의 재사용성을 높이기 위해 분리되었습니다.
  /// </summary>
  public class PipeModelBuilder
  {
    private readonly FeModelContext _context;
    private readonly Dictionary<string, List<int>> _pipeElementIDsByType;
    private readonly bool _debugPrint;

    public PipeModelBuilder(FeModelContext context, Dictionary<string, List<int>> pipeElementIDsByType, bool debugPrint = false)
    {
      _context = context ?? throw new ArgumentNullException(nameof(context));
      _pipeElementIDsByType = pipeElementIDsByType ?? throw new ArgumentNullException(nameof(pipeElementIDsByType));
      _debugPrint = debugPrint;
    }

    /// <summary>
    /// 전달받은 배관 엔티티 리스트를 순회하며 타입별로 적절한 FE 객체를 생성합니다.
    /// </summary>
    /// <param name="pipeList">생성할 배관 데이터 리스트</param>
    public void Build(List<PipeEntity> pipeList)
    {
      foreach (var pipeData in pipeList)
      {
        // 1. 공통 속성 및 질량 변환
        bool isMassValid = double.TryParse(pipeData.Mass, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double massValue);
        int materialID = _context.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

        var extraData = new Dictionary<string, string?>
                {
                    { "Name", pipeData.Name },
                    { "Branch", pipeData.Branch },
                    { "Rest", pipeData.Rest },
                    { "Mass", pipeData.Mass },
                    { "Category", "Pipe" },
                    { "Type", pipeData.Type }
                };

        // 2. 타입별 분기 처리 (Strategy)
        switch (pipeData.Type)
        {
          case "TUBI":
          case "ELBO":
          case "BEND":
            BuildMultiSegmentPipe(pipeData, extraData, materialID);
            break;
          case "OLET":
          case "REDU":
          case "COUP":
            BuildSingleSegmentPipe(pipeData, extraData, materialID);
            break;
          case "TEE":
            BuildTeePipe(pipeData, extraData, materialID);
            break;
          case "FLAN":
            BuildFlange(pipeData, extraData, materialID, isMassValid, massValue);
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
            if (_debugPrint) Console.WriteLine($"[Warning] 알 수 없는 배관 타입입니다: {pipeData.Type}");
            break;
        }
      }
    }

    /// <summary>
    /// 곡관이나 다중 분할이 필요한 일반 배관 요소를 생성합니다.
    /// APos부터 InterPos를 거쳐 LPos까지 노드 체인을 구성합니다.
    /// </summary>
    private void BuildMultiSegmentPipe(PipeEntity pipeData, Dictionary<string, string?> extraData, int materialID)
    {
      double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
      int propertyID = _context.Properties.AddOrGet("TUBE", propertyDim, materialID);

      List<int> nodeChain = new List<int>();
      nodeChain.Add(_context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]));

      if (pipeData.InterPos != null && pipeData.InterPos.Length >= 3)
      {
        for (int i = 0; i < pipeData.InterPos.Length; i += 3)
        {
          nodeChain.Add(_context.Nodes.AddOrGet(pipeData.InterPos[i], pipeData.InterPos[i + 1], pipeData.InterPos[i + 2]));
        }
      }
      nodeChain.Add(_context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]));

      for (int i = 0; i < nodeChain.Count - 1; i++)
      {
        CreateElementSafe(nodeChain[i], nodeChain[i + 1], propertyID, pipeData.Normal, extraData, pipeData);
      }
    }

    /// <summary>
    /// 단일 구간으로 이루어진 배관(OLET, REDU, COUP) 요소를 생성합니다.
    /// </summary>
    private void BuildSingleSegmentPipe(PipeEntity pipeData, Dictionary<string, string?> extraData, int materialID)
    {
      double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
      int propertyID = _context.Properties.AddOrGet("TUBE", propertyDim, materialID);
      double[] barOrientation = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);

      int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
      int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

      CreateElementSafe(startNode, endNode, propertyID, barOrientation, extraData, pipeData);
    }

    /// <summary>
    /// 3방향 분기 배관(TEE) 요소를 생성합니다. 메인 배관과 분기관의 Property를 다르게 적용합니다.
    /// </summary>
    private void BuildTeePipe(PipeEntity pipeData, Dictionary<string, string?> extraData, int materialID)
    {
      int propertyID1 = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim1, pipeData.Dim2 }, materialID);
      int propertyID2 = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim3, pipeData.Dim4 }, materialID);

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

    /// <summary>
    /// 플랜지(FLAN) 데이터를 기반으로 요소와 점 질량(Point Mass)을 함께 생성합니다.
    /// </summary>
    private void BuildFlange(PipeEntity pipeData, Dictionary<string, string?> extraData, int materialID, bool isMassValid, double massValue)
    {
      if (isMassValid && massValue > 0.0)
      {
        int posNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
        _context.PointMasses.AddNew(posNode, massValue, extraData);
      }

      if (pipeData.OutDia > 0)
      {
        int propertyID = _context.Properties.AddOrGet("TUBE", new double[] { pipeData.Dim1, pipeData.Dim2 }, materialID);
        double[] barOrientation = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);
        int startNode = _context.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
        int endNode = _context.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

        CreateElementSafe(startNode, endNode, propertyID, barOrientation, extraData, pipeData);
      }
    }

    /// <summary>
    /// 밸브 및 특수 부속품을 나타내는 강체(Rigid) 요소와 질량(Mass)을 생성합니다.
    /// </summary>
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

      if (isMassValid && massValue > 0.0)
      {
        _context.PointMasses.AddNew(centerNode, massValue, extraData);
      }
    }

    /// <summary>
    /// 3방향 강체 연결 및 질량(VTWA) 모델링을 수행합니다.
    /// </summary>
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

    /// <summary>
    /// UBOLT 데이터를 생성합니다. 
    /// (종속 노드는 마지막 파이프라인 단계에서 구조물에 스냅하여 채워넣습니다.)
    /// </summary>
    private void BuildUBolt(PipeEntity pipeData, Dictionary<string, string?> extraData)
    {
      int indepNode = _context.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
      string restStr = string.IsNullOrWhiteSpace(pipeData.Rest) ? "123456" : pipeData.Rest;

      // Dummy 노드 없이 빈 배열(Array.Empty<int>())로 깔끔하게 생성
      int rbeId = _context.Rigids.AddNew(indepNode, Array.Empty<int>(), restStr, extraData);

      if (_pipeElementIDsByType.ContainsKey(pipeData.Type))
        _pipeElementIDsByType[pipeData.Type].Add(rbeId);
    }

    /// <summary>
    /// 부착 질량(ATTA)을 배관 노드에 바인딩하여 생성합니다.
    /// </summary>
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

    /// <summary>
    /// 요소 생성 시 예외를 캡처하여 전체 프로세스가 중단되지 않도록 보호하는 래퍼 메써드입니다.
    /// </summary>
    private void CreateElementSafe(int n1, int n2, int propId, double[] ori, Dictionary<string, string?> extra, PipeEntity pipe)
    {
      if (n1 == n2) return;
      try
      {
        int eid = _context.Elements.AddNew(new List<int> { n1, n2 }, propId, ori, extra);
        if (_pipeElementIDsByType.ContainsKey(pipe.Type)) _pipeElementIDsByType[pipe.Type].Add(eid);
      }
      catch (Exception ex)
      {
        if (_debugPrint)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"[Error] {pipe.Type} 생성 실패! Name: {pipe.Name} / 에러: {ex.Message}");
          Console.ResetColor();
        }
      }
    }
  }
}
