using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;
using _2025_Skid.Model;
using _2025_Skid.Utils;


namespace _2025_Skid.Control
{
  public class CsvParse
  {
    string pipeCsv;
    string equipCsv;
    Materials materialInstance;
    Properties propertyInstance;
    Nodes nodeInstance;
    Elements elementInstance;
    RBEs rbeInstance;
    Conm conmInstrance;
    public List<int> valveElementID = new List<int>();
    public List<int> BoundaryCondition_list = new List<int>();
    public List<int> uboltNodeId_list = new List<int>();
    public List<int> uboltConnectionNodeId_list = new List<int>();
    public Dictionary<string, List<int>> pipeElementIDsByType = new()
    {
        { "TUBI", new List<int>() },
        { "OLET", new List<int>() },
        { "FLAN", new List<int>() },
        { "REDU", new List<int>() },
        { "TEE", new List<int>() },
        { "BEND", new List<int>() },
        { "ELBO", new List<int>() },
        { "VALV", new List<int>() },
        { "VTWA", new List<int>() }
    };


    public CsvParse(string pipeData, string equipData,
      Materials materialInstance, Properties propertyInstance, Nodes nodeInstance, Elements elementInstance,
       RBEs rbeInstance, Conm conmInstrance)
    {
      this.pipeCsv = pipeData;
      this.equipCsv = equipData;
      this.materialInstance = materialInstance;
      this.propertyInstance = propertyInstance;
      this.nodeInstance = nodeInstance;
      this.elementInstance = elementInstance;
      this.rbeInstance = rbeInstance;
      this.conmInstrance = conmInstrance;
    }

    public Dictionary<string, List<int>> Run()
    {
      PipeParse();
      EquipParse();

      return pipeElementIDsByType;
    }
  

    public void PipeParse()
    {
      //Console.WriteLine($"propertyID in CsvParse : {propertyInstance.propertyID}");
        // 모든 CSV 행을 가지고 오기, 행이 많이 많기 때문에 ReadAllLines 사용
      var lines = File.ReadAllLines(this.pipeCsv);

      // 좌표를 분리하는 정규표현식
      Regex pointsRegex = new Regex(@"-?\d+(\.\d+)?");

      foreach (var line in lines)
      {
        // 첫 번째 행 (인덱스 행)일 경우 건너뛰기
        if (Array.IndexOf(lines, line) == 0)
        {
          continue;  // 첫 번째 행을 건너뛰고 다음 행으로 이동
        }
        // Node 시작, 종료 절대 좌표 파싱
        string[] values = line.Split(',');

        // values[1] : Pipe의 Type
        string type = values[1];

        // values[2], values[3], values[4] : Pipe의 pos, apos, lpos
        MatchCollection pos = pointsRegex.Matches(values[2]);
        MatchCollection apos = pointsRegex.Matches(values[3]);
        MatchCollection lpos = pointsRegex.Matches(values[4]);

        double[] posArray = pos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        double[] aposArray = apos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        double[] lposArray = lpos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();


        // values[6], values[7] : 외경, 두께
        double outDia = double.Parse(values[6]);
        double outRad = outDia / 2;
        double thick = double.Parse(values[7]);
        double innerRad = outRad - thick;

        // values[8] : Normal 값 가지고 오기, 존재하지 않는다면 모두 0.0으로 지정
        double[] normal;
        if (string.IsNullOrWhiteSpace(values[8]))
        {
          normal = new double[] { 0.0, 0.0, 0.0 }; // 또는 new double[0];
        }
        else
        {
          string[] normalString = values[8].Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);
          normal = normalString.Select(double.Parse).ToArray();
        }

        // values[9] : interPos는 안씀
        // values[10] : p3Pos, Tee에 존재하는 분기점, 존재하지 않는다면 모두 0.0로 지정
        double[]? p3Pos = null;
        if (!string.IsNullOrWhiteSpace(values[10]))
        {
          MatchCollection p3PosCollection = pointsRegex.Matches(values[10]);
          p3Pos = p3PosCollection.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        }

        // values[11], values[12] : 외경2, 두께2
        double outDia2 = double.Parse(values[11]);
        double outRad2 = outDia2 / 2;
        double thick2 = double.Parse(values[12]);
        double innerRad2 = outRad2 - thick2;

        // values[13] : rest, Ubolt 경계조건 자유도 
        int[]? rest = null;
        string restString = values[13].Trim();

        // values[14] : mass
        double mass = double.Parse(values[14]);

        if (!string.IsNullOrWhiteSpace(restString))
        {
          rest = restString.Select(ch => int.Parse(ch.ToString())).ToArray();
        }

        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["type"] = type;
        extraData["Category"] = "Pipe";

        // TUBI, OLET, FLAN, REDU 모두 일반 배관형태로 OD와 두께를 가짐, 일반 배관으로 처리
        if ((type == "TUBI") || (type == "OLET") || (type == "FLAN") || (type == "REDU"))
        {
          // type마다 Material을 생성, 향후 Type 별로 중량 맞추기 위함
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);

          double[] propertyDim = new double[] { outRad, innerRad };
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);
          //Console.WriteLine($"propertyID : {propertyID}");

          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementID = elementInstance.AddNew(new List<int> { startNodeID, endNodeID },
            propertyID, normal, extraData);
          if (pipeElementIDsByType.ContainsKey(type))
            pipeElementIDsByType[type].Add(elementID);
        }

        else if (type == "TRAP")
        {
          // type마다 Material을 생성, 향후 Type 별로 중량 맞추기 위함
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);

          double[] propertyDim = new double[] { 20.0, 10.0 }; // Trap은 자동 할당이 어려워서 고정
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);
          double tol = 1;
          normal = new double[3];

          // 첫번째 valve element
          double dx = posArray[0] - aposArray[0];
          double dy = posArray[1] - aposArray[1];
          double dz = posArray[2] - aposArray[2];
          // XY 평면 (Z가 거의 일정)
          if (Math.Abs(dz) < tol && Math.Abs(dx) > tol && Math.Abs(dy) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }

          else if (Math.Abs(dy) > tol && Math.Abs(dx) < tol && Math.Abs(dz) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }

          else if (Math.Abs(dx) < tol && Math.Abs(dy) < tol && Math.Abs(dz) > tol)
          {
            normal[0] = 1.0;
            normal[1] = 0.0;
            normal[2] = 0.0;
          }
          else if (Math.Abs(dx) > tol && Math.Abs(dy) > tol && Math.Abs(dz) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }
          else if (Math.Abs(dx) > tol && Math.Abs(dy) < tol && Math.Abs(dz) > tol)
          {
            normal[0] = 1.0;
            normal[1] = 0.0;
            normal[2] = 0.0;
          }
          else if (Math.Abs(dx) < tol && Math.Abs(dy) > tol && Math.Abs(dz) > tol)
          {
            normal[0] = 0.0;
            normal[1] = 1.0;
            normal[2] = 0.0;
          }
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementID1 = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID },
            propertyID, normal, extraData);
          int elementID2 = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID },
            propertyID, normal, extraData);
          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(elementID1);
            pipeElementIDsByType[type].Add(elementID2);
          }

        }

        // TEE 타입 처리
        else if (type == "TEE")
        {
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);
          // TEE는 연결되는 2개의 부재 property가 다를 수도 있기에 각각 생성
          double[] propertyDim1 = new double[] { outRad, innerRad };
          double[] propertyDim2 = new double[] { outRad2, innerRad2 };
          int propertyID1 = propertyInstance.AddOrGet("TUBE", propertyDim1, materialID, extraData);
          int propertyID2 = propertyInstance.AddOrGet("TUBE", propertyDim2, materialID, extraData);

          // 세 포인트를 이용하여 각각 Node 생성
          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          int p3PosNodeID = nodeInstance.AddOrGet(p3Pos[0], p3Pos[1], p3Pos[2]);

          int elementStartBetweenID = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID }, propertyID1, normal, extraData);
          int elementBetweenEndID = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID }, propertyID1, normal, extraData);
          int elementEndP3Pos = elementInstance.AddNew(new List<int> { betweenNodeID, p3PosNodeID }, propertyID2, normal, extraData);
          pipeElementIDsByType["TEE"].Add(elementStartBetweenID);
          pipeElementIDsByType["TEE"].Add(elementBetweenEndID);
          pipeElementIDsByType["TEE"].Add(elementEndP3Pos);
        }

        // ELBO, BEND 타입 처리
        else if ((type == "ELBO") || (type == "BEND"))
        {
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);
          // TEE는 연결되는 2개의 부재 property가 다를 수도 있기에 각각 생성
          double[] propertyDim = new double[] { outRad, innerRad };
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);

          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementStartBetweenID = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID }, propertyID, normal, extraData);
          int elementBetweenEndID = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID }, propertyID, normal, extraData);

          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(elementStartBetweenID);
            pipeElementIDsByType[type].Add(elementBetweenEndID);
          }
        }

        // VALV 타입 처리
        else if (type == "VALV")
        {
          // RBE의 ID를 부재 Element와 이어주기 위한 동기화 메써드 호출
          // 초반엔 rigid로 모델링 했으나 rigid끼리 엮이는 것들을 방지하기 위해, 배관으로 변경 필요 
          //rbeInstance.SynchronizeRbeIDWithElements();
          //int independentID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          //int dependencNodesID1 = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          //int dependencNodesID2 = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          //int rbeID = rbeInstance.AddOrGet(independentID, new int[] { dependencNodesID1, dependencNodesID2 }, extraData);

          //if (pipeElementIDsByType.ContainsKey(type))
          //{
          //  pipeElementIDsByType[type].Add(rbeID);
          //}

          int independentID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int dependencNodesID1 = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int dependencNodesID2 = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          double tol = 1;
          double[] normalA = new double[3];
          double[] normalB = new double[3];
          int propertyID = propertyInstance.AddOrGet("TUBE", [2.0, 1.0], 1, extraData);

          // 첫번째 valve element
          double dxA = posArray[0] - aposArray[0];
          double dyA = posArray[1] - aposArray[1];
          double dzA = posArray[2] - aposArray[2];
          // XY 평면 (Z가 거의 일정)
          if (Math.Abs(dzA) < tol && Math.Abs(dxA) > tol && Math.Abs(dyA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }

          else if (Math.Abs(dyA) > tol && Math.Abs(dxA) < tol && Math.Abs(dzA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }

          else if (Math.Abs(dxA) < tol && Math.Abs(dyA) < tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 1.0;
            normalA[1] = 0.0;
            normalA[2] = 0.0;
          }
          else if (Math.Abs(dxA) > tol && Math.Abs(dyA) > tol && Math.Abs(dzA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }
          else if (Math.Abs(dxA) > tol && Math.Abs(dyA) < tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 1.0;
            normalA[1] = 0.0;
            normalA[2] = 0.0;
          }
          else if (Math.Abs(dxA) < tol && Math.Abs(dyA) > tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 1.0;
            normalA[2] = 0.0;
          }

          // 두번째 valve element
          double dxB = posArray[0] - lposArray[0];
          double dyB = posArray[1] - lposArray[1];
          double dzB = posArray[2] - lposArray[2];

          if (Math.Abs(dzB) < tol && Math.Abs(dxB) > tol && Math.Abs(dyB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }

          else if (Math.Abs(dyB) > tol && Math.Abs(dxB) < tol && Math.Abs(dzB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }

          else if (Math.Abs(dxB) < tol && Math.Abs(dyB) < tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 1.0;
            normalB[1] = 0.0;
            normalB[2] = 0.0;
          }
          else if (Math.Abs(dxB) > tol && Math.Abs(dyB) > tol && Math.Abs(dzB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }
          else if (Math.Abs(dxB) > tol && Math.Abs(dyB) < tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 1.0;
            normalB[1] = 0.0;
            normalB[2] = 0.0;
          }
          else if (Math.Abs(dxB) < tol && Math.Abs(dyB) > tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 1.0;
            normalB[2] = 0.0;
          }

          int valveEleA = elementInstance.AddNew(new List<int> { dependencNodesID1, independentID }, propertyID, normalA, extraData);
          int valveEleB = elementInstance.AddNew(new List<int> { independentID, dependencNodesID2 }, propertyID, normalB, extraData);
          valveElementID.Add(valveEleA);
          valveElementID.Add(valveEleB);
          //Console.WriteLine($"생성되는 ElementA : {valveEleA},{elementInstance[valveEleA]}, {dxA},{dyA},{dzA}");
          //Console.WriteLine($"생성되는 ElementB : {valveEleB},{elementInstance[valveEleB]}, {dxB},{dyB},{dzB}");

          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(valveEleA);
            pipeElementIDsByType[type].Add(valveEleB);
          }
        }

        // TEE 타입 처리
        else if (type == "UBOLT")
        {
          int UboltConnectionNode = 0;
          //Console.WriteLine($"{string.Join(",", normal)}");
          int UboltNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);

          if ((normal[0] == 0.0) && (normal[1] == 0.0) && (normal[2] != 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2] - 50);
          }
          else if ((normal[0] != 0.0) && (normal[1] == 0.0) && (normal[2] == 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0] + (normal[0] * -50), posArray[1], posArray[2]);
          }
          else if ((normal[0] == 0.0) && (normal[1] != 0.0) && (normal[2] == 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0], posArray[1] + (normal[1] * -50), posArray[2]);
          }

          int uboltRbeID = rbeInstance.AddOrGet(UboltNode, new int[] { UboltConnectionNode });
          uboltNodeId_list.Add(UboltNode);
          uboltConnectionNodeId_list.Add(UboltConnectionNode);
        }

        // Point Mass 처리, "FBLI"
        else if (type == "VTWA")
        {
          double[] VTWA_Dim = [35.0, 7.0];
          int beforeNode = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int betweenNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int afterNode = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          int downNode = nodeInstance.AddOrGet(p3Pos[0], p3Pos[1], p3Pos[2]);

          double[] before_between_normal = GeometryUtils.CalculateNormalVector(aposArray[0] - posArray[0],
            aposArray[1] - posArray[1], aposArray[2] - posArray[2]);
          double[] between_after_normal = GeometryUtils.CalculateNormalVector(posArray[0] - lposArray[0],
            posArray[1] - lposArray[1], posArray[2] - lposArray[2]);
          double[] between_down_normal = GeometryUtils.CalculateNormalVector(posArray[0] - p3Pos[0],
            posArray[1] - p3Pos[1], posArray[2] - p3Pos[2]);

          int propertyID_before_between = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);
          int propertyID_between_after = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);
          int propertyID_between_down = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);

          int element_before_betweenID = elementInstance.AddNew(new List<int> { beforeNode, betweenNode }, 
            propertyID_before_between, before_between_normal, extraData);
          int element_between_afterID = elementInstance.AddNew(new List<int> { betweenNode, afterNode }, 
            propertyID_between_after, between_after_normal, extraData);
          int element_between_downID = elementInstance.AddNew(new List<int> { betweenNode, downNode }, 
            propertyID_between_down, between_down_normal, extraData);

          //int ConmID = conmInstrance.AddNew(new double[] { aposArray[0], aposArray[1], aposArray[2] }, mass, extraData);

          //if (pipeElementIDsByType.ContainsKey(type))
          //{
          //  pipeElementIDsByType[type].Add(ConmID);
          //}
        }
      }
    }

    public void EquipParse()
    {
      var lines = File.ReadAllLines(this.equipCsv);
      Regex pointsRegex = new Regex(@"-?\d+(\.\d+)?");

      // 하나 열씩 가지고 오기, 인덱스 행 넘기기 
      foreach (var line in lines.Skip(1))
      {

        string[] cols = line.Split(',');

        // mass가 0이면 제낀다. continue
        if (double.TryParse(cols[4], out double mass))
        {
          if (mass == 0.0) continue;        
        }

        MatchCollection cogPos = pointsRegex.Matches(cols[2]);
        double[] cogArray = cogPos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();   
        // 3번 인덱스 즉 interPos가 없으면 rigid 연결이 아닌 Node에 직접 point mass를 생성하는 케이스 
        // 이때 공중에 Point mass가 존재하면 안되니까 구조에 tolerance 안에 들어오는 Node가 있는지 찾고 있으면 거기 할당
        if (string.IsNullOrWhiteSpace(cols[3]))
        {
          // 거리 10이내에 존재하는 Node 있는지 찾고 없으면 point mass 자체를 생성 안한다. 
          int equipNodeID =
            nodeInstance.FindNodeWithinTolerance(cogArray[0], cogArray[1], cogArray[2], tolerance: 10);

          // tolerance 내에 존재하는 node가 없다면 나가기 
          if (equipNodeID == -1) continue;

          conmInstrance.AddNew(cogArray, mass*0.001);
          
        }
        // interPos에 값이 있다면 cog는 independenct 나머지는 dependent로 rigid 생성 필요 
        else
        {
          // cog의 node를 생성하기 
          int cogNodeID = nodeInstance.AddOrGet(cogArray[0], cogArray[1], cogArray[2]);
          List<int> dependentNode_list = new List<int>();

          string rawInterPos = cols[3].Trim(); // 앞뒤 공백 제거

          // '+' 있으면 여러 개로, 없으면 1개짜리 배열로 들어옴
          string[] interPoses = rawInterPos.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
          
          // rigid의 dependent 좌표들을 모아둘 List
          List<double[]> dependentNodes = new List<double[]>();

          foreach (var pos in interPoses)
          {
            MatchCollection interPos = pointsRegex.Matches(pos);
            double[] interPosArray = interPos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
            dependentNodes.Add(interPosArray);
          }
          
          foreach(var pos in dependentNodes)
          {
            //Console.WriteLine(string.Join(",", pos));
            int dependentNodeID = nodeInstance.FindNodeWithinTolerance(pos[0], pos[1], pos[2], tolerance: 10);
            //Console.WriteLine($"{string.Join(",", pos)}, dependentNodeID : {dependentNodeID},{(dependentNodeID == -1)}");

            // 이건 rigid의 dependent 위치의 node를 못찾았기에 continue로 제낀다. 
            if (dependentNodeID == -1)
            {
              continue;
            }
            dependentNode_list.Add(dependentNodeID);
          }

          // 만약 independent 하나도 없다면 넘기기
          if (dependentNode_list.Count == 0)
          {
            continue;  
          }

          // 배열로 변경
          int[] dependenctNode_array = dependentNode_list.ToArray();

          int rbeID = rbeInstance.AddOrGet(cogNodeID, dependenctNode_array);
          //Console.WriteLine($"{rbeID}, Nodes : {string.Join(",", dependenctNode_array)}");
        }

        //Console.WriteLine($"{string.Join(",", cols)}, 열 개수:{cols.Length}");
      }
    }
  }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;
using _2025_Skid.Model;
using _2025_Skid.Utils;


namespace _2025_Skid.Control
{
  public class CsvParse
  {
    string pipeCsv;
    string equipCsv;
    Materials materialInstance;
    Properties propertyInstance;
    Nodes nodeInstance;
    Elements elementInstance;
    RBEs rbeInstance;
    Conm conmInstrance;
    public List<int> valveElementID = new List<int>();
    public List<int> BoundaryCondition_list = new List<int>();
    public List<int> uboltNodeId_list = new List<int>();
    public List<int> uboltConnectionNodeId_list = new List<int>();
    public Dictionary<string, List<int>> pipeElementIDsByType = new()
    {
        { "TUBI", new List<int>() },
        { "OLET", new List<int>() },
        { "FLAN", new List<int>() },
        { "REDU", new List<int>() },
        { "TEE", new List<int>() },
        { "BEND", new List<int>() },
        { "ELBO", new List<int>() },
        { "VALV", new List<int>() },
        { "VTWA", new List<int>() }
    };


    public CsvParse(string pipeData, string equipData,
      Materials materialInstance, Properties propertyInstance, Nodes nodeInstance, Elements elementInstance,
       RBEs rbeInstance, Conm conmInstrance)
    {
      this.pipeCsv = pipeData;
      this.equipCsv = equipData;
      this.materialInstance = materialInstance;
      this.propertyInstance = propertyInstance;
      this.nodeInstance = nodeInstance;
      this.elementInstance = elementInstance;
      this.rbeInstance = rbeInstance;
      this.conmInstrance = conmInstrance;
    }

    public Dictionary<string, List<int>> Run()
    {
      PipeParse();
      EquipParse();

      return pipeElementIDsByType;
    }
  

    public void PipeParse()
    {
      //Console.WriteLine($"propertyID in CsvParse : {propertyInstance.propertyID}");
        // 모든 CSV 행을 가지고 오기, 행이 많이 많기 때문에 ReadAllLines 사용
      var lines = File.ReadAllLines(this.pipeCsv);

      // 좌표를 분리하는 정규표현식
      Regex pointsRegex = new Regex(@"-?\d+(\.\d+)?");

      foreach (var line in lines)
      {
        // 첫 번째 행 (인덱스 행)일 경우 건너뛰기
        if (Array.IndexOf(lines, line) == 0)
        {
          continue;  // 첫 번째 행을 건너뛰고 다음 행으로 이동
        }
        // Node 시작, 종료 절대 좌표 파싱
        string[] values = line.Split(',');

        // values[1] : Pipe의 Type
        string type = values[1];

        // values[2], values[3], values[4] : Pipe의 pos, apos, lpos
        MatchCollection pos = pointsRegex.Matches(values[2]);
        MatchCollection apos = pointsRegex.Matches(values[3]);
        MatchCollection lpos = pointsRegex.Matches(values[4]);

        double[] posArray = pos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        double[] aposArray = apos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        double[] lposArray = lpos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();


        // values[6], values[7] : 외경, 두께
        double outDia = double.Parse(values[6]);
        double outRad = outDia / 2;
        double thick = double.Parse(values[7]);
        double innerRad = outRad - thick;

        // values[8] : Normal 값 가지고 오기, 존재하지 않는다면 모두 0.0으로 지정
        double[] normal;
        if (string.IsNullOrWhiteSpace(values[8]))
        {
          normal = new double[] { 0.0, 0.0, 0.0 }; // 또는 new double[0];
        }
        else
        {
          string[] normalString = values[8].Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);
          normal = normalString.Select(double.Parse).ToArray();
        }

        // values[9] : interPos는 안씀
        // values[10] : p3Pos, Tee에 존재하는 분기점, 존재하지 않는다면 모두 0.0으로 지정
        double[]? p3Pos = null;
        if (!string.IsNullOrWhiteSpace(values[10]))
        {
          MatchCollection p3PosCollection = pointsRegex.Matches(values[10]);
          p3Pos = p3PosCollection.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
        }

        // values[11], values[12] : 외경2, 두께2
        double outDia2 = double.Parse(values[11]);
        double outRad2 = outDia2 / 2;
        double thick2 = double.Parse(values[12]);
        double innerRad2 = outRad2 - thick2;

        // values[13] : rest, Ubolt 경계조 자유도 
        int[]? rest = null;
        string restString = values[13].Trim();

        // values[14] : mass
        double mass = double.Parse(values[14]);

        if (!string.IsNullOrWhiteSpace(restString))
        {
          rest = restString.Select(ch => int.Parse(ch.ToString())).ToArray();
        }

        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["type"] = type;
        extraData["Category"] = "Pipe";

        // TUBI, OLET, FLAN, REDU 모두 일반 배관형태로 OD와 두께를 가짐, 일반 배관으로 처리
        if ((type == "TUBI") || (type == "OLET") || (type == "FLAN") || (type == "REDU"))
        {
          // type마다 Material을 생성, 향후 Type 별로 중량 맞추기 위함
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);

          double[] propertyDim = new double[] { outRad, innerRad };
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);
          //Console.WriteLine($"propertyID : {propertyID}");

          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementID = elementInstance.AddNew(new List<int> { startNodeID, endNodeID },
            propertyID, normal, extraData);
          if (pipeElementIDsByType.ContainsKey(type))
            pipeElementIDsByType[type].Add(elementID);
        }

        else if (type == "TRAP")
        {
          // type마다 Material을 생성, 향후 Type 별로 중량 맞추기 위함
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);

          double[] propertyDim = new double[] { 20.0, 10.0 }; // Trap은 자동 할당이 어려워서 고정
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);
          double tol = 1;
          normal = new double[3];

          // 첫번째 valve element
          double dx = posArray[0] - aposArray[0];
          double dy = posArray[1] - aposArray[1];
          double dz = posArray[2] - aposArray[2];
          // XY 평면 (Z가 거의 일정)
          if (Math.Abs(dz) < tol && Math.Abs(dx) > tol && Math.Abs(dy) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }

          else if (Math.Abs(dy) > tol && Math.Abs(dx) < tol && Math.Abs(dz) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }

          else if (Math.Abs(dx) < tol && Math.Abs(dy) < tol && Math.Abs(dz) > tol)
          {
            normal[0] = 1.0;
            normal[1] = 0.0;
            normal[2] = 0.0;
          }
          else if (Math.Abs(dx) > tol && Math.Abs(dy) > tol && Math.Abs(dz) < tol)
          {
            normal[0] = 0.0;
            normal[1] = 0.0;
            normal[2] = 1.0;
          }
          else if (Math.Abs(dx) > tol && Math.Abs(dy) < tol && Math.Abs(dz) > tol)
          {
            normal[0] = 1.0;
            normal[1] = 0.0;
            normal[2] = 0.0;
          }
          else if (Math.Abs(dx) < tol && Math.Abs(dy) > tol && Math.Abs(dz) > tol)
          {
            normal[0] = 0.0;
            normal[1] = 1.0;
            normal[2] = 0.0;
          }
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementID1 = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID },
            propertyID, normal, extraData);
          int elementID2 = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID },
            propertyID, normal, extraData);
          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(elementID1);
            pipeElementIDsByType[type].Add(elementID2);
          }

        }

        // TEE 타입 처리
        else if (type == "TEE")
        {
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);
          // TEE는 연결되는 2개의 부재 property가 다를 수도 있기에 각각 생성
          double[] propertyDim1 = new double[] { outRad, innerRad };
          double[] propertyDim2 = new double[] { outRad2, innerRad2 };
          int propertyID1 = propertyInstance.AddOrGet("TUBE", propertyDim1, materialID, extraData);
          int propertyID2 = propertyInstance.AddOrGet("TUBE", propertyDim2, materialID, extraData);

          // 세 포인트를 이용하여 각각 Node 생성
          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          int p3PosNodeID = nodeInstance.AddOrGet(p3Pos[0], p3Pos[1], p3Pos[2]);

          int elementStartBetweenID = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID }, propertyID1, normal, extraData);
          int elementBetweenEndID = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID }, propertyID1, normal, extraData);
          int elementEndP3Pos = elementInstance.AddNew(new List<int> { betweenNodeID, p3PosNodeID }, propertyID2, normal, extraData);
          pipeElementIDsByType["TEE"].Add(elementStartBetweenID);
          pipeElementIDsByType["TEE"].Add(elementBetweenEndID);
          pipeElementIDsByType["TEE"].Add(elementEndP3Pos);
        }

        // ELBO, BEND 타입 처리
        else if ((type == "ELBO") || (type == "BEND"))
        {
          int materialID = materialInstance.AddOrGet(206000, 0.03, 7.85e-09, extraData);
          // TEE는 연결되는 2개의 부재 property가 다를 수도 있기에 각각 생성
          double[] propertyDim = new double[] { outRad, innerRad };
          int propertyID = propertyInstance.AddOrGet("TUBE", propertyDim, materialID, extraData);

          int betweenNodeID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int startNodeID = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int endNodeID = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          int elementStartBetweenID = elementInstance.AddNew(new List<int> { startNodeID, betweenNodeID }, propertyID, normal, extraData);
          int elementBetweenEndID = elementInstance.AddNew(new List<int> { betweenNodeID, endNodeID }, propertyID, normal, extraData);

          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(elementStartBetweenID);
            pipeElementIDsByType[type].Add(elementBetweenEndID);
          }
        }

        // VALV 타입 처리
        else if (type == "VALV")
        {
          // RBE의 ID를 부재 Element와 이어주기 위한 동기화 메써드 호출
          // 초반엔 rigid로 모델링 했으나 rigid끼리 엮이는 것들을 방지하기 위해, 배관으로 변경 필요 
          //rbeInstance.SynchronizeRbeIDWithElements();
          //int independentID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          //int dependencNodesID1 = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          //int dependencNodesID2 = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);

          //int rbeID = rbeInstance.AddOrGet(independentID, new int[] { dependencNodesID1, dependencNodesID2 }, extraData);

          //if (pipeElementIDsByType.ContainsKey(type))
          //{
          //  pipeElementIDsByType[type].Add(rbeID);
          //}

          int independentID = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int dependencNodesID1 = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int dependencNodesID2 = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          double tol = 1;
          double[] normalA = new double[3];
          double[] normalB = new double[3];
          int propertyID = propertyInstance.AddOrGet("TUBE", [2.0, 1.0], 1, extraData);

          // 첫번째 valve element
          double dxA = posArray[0] - aposArray[0];
          double dyA = posArray[1] - aposArray[1];
          double dzA = posArray[2] - aposArray[2];
          // XY 평면 (Z가 거의 일정)
          if (Math.Abs(dzA) < tol && Math.Abs(dxA) > tol && Math.Abs(dyA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }

          else if (Math.Abs(dyA) > tol && Math.Abs(dxA) < tol && Math.Abs(dzA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }

          else if (Math.Abs(dxA) < tol && Math.Abs(dyA) < tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 1.0;
            normalA[1] = 0.0;
            normalA[2] = 0.0;
          }
          else if (Math.Abs(dxA) > tol && Math.Abs(dyA) > tol && Math.Abs(dzA) < tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 0.0;
            normalA[2] = 1.0;
          }
          else if (Math.Abs(dxA) > tol && Math.Abs(dyA) < tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 1.0;
            normalA[1] = 0.0;
            normalA[2] = 0.0;
          }
          else if (Math.Abs(dxA) < tol && Math.Abs(dyA) > tol && Math.Abs(dzA) > tol)
          {
            normalA[0] = 0.0;
            normalA[1] = 1.0;
            normalA[2] = 0.0;
          }

          // 두번째 valve element
          double dxB = posArray[0] - lposArray[0];
          double dyB = posArray[1] - lposArray[1];
          double dzB = posArray[2] - lposArray[2];

          if (Math.Abs(dzB) < tol && Math.Abs(dxB) > tol && Math.Abs(dyB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }

          else if (Math.Abs(dyB) > tol && Math.Abs(dxB) < tol && Math.Abs(dzB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }

          else if (Math.Abs(dxB) < tol && Math.Abs(dyB) < tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 1.0;
            normalB[1] = 0.0;
            normalB[2] = 0.0;
          }
          else if (Math.Abs(dxB) > tol && Math.Abs(dyB) > tol && Math.Abs(dzB) < tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 0.0;
            normalB[2] = 1.0;
          }
          else if (Math.Abs(dxB) > tol && Math.Abs(dyB) < tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 1.0;
            normalB[1] = 0.0;
            normalB[2] = 0.0;
          }
          else if (Math.Abs(dxB) < tol && Math.Abs(dyB) > tol && Math.Abs(dzB) > tol)
          {
            normalB[0] = 0.0;
            normalB[1] = 1.0;
            normalB[2] = 0.0;
          }

          int valveEleA = elementInstance.AddNew(new List<int> { dependencNodesID1, independentID }, propertyID, normalA, extraData);
          int valveEleB = elementInstance.AddNew(new List<int> { independentID, dependencNodesID2 }, propertyID, normalB, extraData);
          valveElementID.Add(valveEleA);
          valveElementID.Add(valveEleB);
          //Console.WriteLine($"생성되는 ElementA : {valveEleA},{elementInstance[valveEleA]}, {dxA},{dyA},{dzA}");
          //Console.WriteLine($"생성되는 ElementB : {valveEleB},{elementInstance[valveEleB]}, {dxB},{dyB},{dzB}");

          if (pipeElementIDsByType.ContainsKey(type))
          {
            pipeElementIDsByType[type].Add(valveEleA);
            pipeElementIDsByType[type].Add(valveEleB);
          }
        }

        // TEE 타입 처리
        else if (type == "UBOLT")
        {
          int UboltConnectionNode = 0;
          //Console.WriteLine($"{string.Join(",", normal)}");
          int UboltNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);

          if ((normal[0] == 0.0) && (normal[1] == 0.0) && (normal[2] != 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2] - 50);
          }
          else if ((normal[0] != 0.0) && (normal[1] == 0.0) && (normal[2] == 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0] + (normal[0] * -50), posArray[1], posArray[2]);
          }
          else if ((normal[0] == 0.0) && (normal[1] != 0.0) && (normal[2] == 0.0))
          {
            UboltConnectionNode = nodeInstance.AddOrGet(posArray[0], posArray[1] + (normal[1] * -50), posArray[2]);
          }

          int uboltRbeID = rbeInstance.AddOrGet(UboltNode, new int[] { UboltConnectionNode });
          uboltNodeId_list.Add(UboltNode);
          uboltConnectionNodeId_list.Add(UboltConnectionNode);
        }

        // Point Mass 처리, "FBLI"
        else if (type == "VTWA")
        {
          double[] VTWA_Dim = [35.0, 7.0];
          int beforeNode = nodeInstance.AddOrGet(aposArray[0], aposArray[1], aposArray[2]);
          int betweenNode = nodeInstance.AddOrGet(posArray[0], posArray[1], posArray[2]);
          int afterNode = nodeInstance.AddOrGet(lposArray[0], lposArray[1], lposArray[2]);
          int downNode = nodeInstance.AddOrGet(p3Pos[0], p3Pos[1], p3Pos[2]);

          double[] before_between_normal = GeometryUtils.CalculateNormalVector(aposArray[0] - posArray[0],
            aposArray[1] - posArray[1], aposArray[2] - posArray[2]);
          double[] between_after_normal = GeometryUtils.CalculateNormalVector(posArray[0] - lposArray[0],
            posArray[1] - lposArray[1], posArray[2] - lposArray[2]);
          double[] between_down_normal = GeometryUtils.CalculateNormalVector(posArray[0] - p3Pos[0],
            posArray[1] - p3Pos[1], posArray[2] - p3Pos[2]);

          int propertyID_before_between = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);
          int propertyID_between_after = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);
          int propertyID_between_down = propertyInstance.AddOrGet("TUBE", VTWA_Dim, 1, extraData);

          int element_before_betweenID = elementInstance.AddNew(new List<int> { beforeNode, betweenNode }, 
            propertyID_before_between, before_between_normal, extraData);
          int element_between_afterID = elementInstance.AddNew(new List<int> { betweenNode, afterNode }, 
            propertyID_between_after, between_after_normal, extraData);
          int element_between_downID = elementInstance.AddNew(new List<int> { betweenNode, downNode }, 
            propertyID_between_down, between_down_normal, extraData);

          //int ConmID = conmInstrance.AddNew(new double[] { aposArray[0], aposArray[1], aposArray[2] }, mass, extraData);

          //if (pipeElementIDsByType.ContainsKey(type))
          //{
          //  pipeElementIDsByType[type].Add(ConmID);
          //}
        }
      }
    }

    public void EquipParse()
    {
      var lines = File.ReadAllLines(this.equipCsv);
      Regex pointsRegex = new Regex(@"-?\d+(\.\d+)?");

      // 하나 열씩 가지고 오기, 인덱스 행 넘기기 
      foreach (var line in lines.Skip(1))
      {

        string[] cols = line.Split(',');

        // mass가 0이면 제낀다. continue
        if (double.TryParse(cols[4], out double mass))
        {
          if (mass == 0.0) continue;        
        }

        MatchCollection cogPos = pointsRegex.Matches(cols[2]);
        double[] cogArray = cogPos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();   
        // 3번 인덱스 즉 interPos가 없으면 rigid 연결이 아닌 Node에 직접 point mass를 생성하는 케이스 
        // 이때 공중에 Point mass가 존재하면 안되니까 구조에 tolerance 안에 들어오는 Node가 있는지 찾고 있으면 거기 할당
        if (string.IsNullOrWhiteSpace(cols[3]))
        {
          // 거리 10이내에 존재하는 Node 있는지 찾고 없으면 point mass 자체를 생성 안한다. 
          int equipNodeID =
            nodeInstance.FindNodeWithinTolerance(cogArray[0], cogArray[1], cogArray[2], tolerance: 10);

          // tolerance 내에 존재하는 node가 없다면 나가기 
          if (equipNodeID == -1) continue;

          conmInstrance.AddNew(cogArray, mass*0.001);
          
        }
        // interPos에 값이 있다면 cog는 independenct 나머지는 dependent로 rigid 생성 필요 
        else
        {
          // cog의 node를 생성하기 
          int cogNodeID = nodeInstance.AddOrGet(cogArray[0], cogArray[1], cogArray[2]);
          List<int> dependentNode_list = new List<int>();

          string rawInterPos = cols[3].Trim(); // 앞뒤 공백 제거

          // '+' 있으면 여러 개로, 없으면 1개짜리 배열로 들어옴
          string[] interPoses = rawInterPos.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
          
          // rigid의 dependent 좌표들을 모아둘 List
          List<double[]> dependentNodes = new List<double[]>();

          foreach (var pos in interPoses)
          {
            MatchCollection interPos = pointsRegex.Matches(pos);
            double[] interPosArray = interPos.Cast<Match>().Select(m => double.Parse(m.Value)).ToArray();
            dependentNodes.Add(interPosArray);
          }
          
          foreach(var pos in dependentNodes)
          {
            //Console.WriteLine(string.Join(",", pos));
            int dependentNodeID = nodeInstance.FindNodeWithinTolerance(pos[0], pos[1], pos[2], tolerance: 10);
            //Console.WriteLine($"{string.Join(",", pos)}, dependentNodeID : {dependentNodeID},{(dependentNodeID == -1)}");

            // 이건 rigid의 dependent 위치의 node를 못찾았기에 continue로 제낀다. 
            if (dependentNodeID == -1)
            {
              continue;
            }
            dependentNode_list.Add(dependentNodeID);
          }

          // 만약 independent 하나도 없다면 넘기기
          if (dependentNode_list.Count == 0)
          {
            continue;  
          }

          // 배열로 변경
          int[] dependenctNode_array = dependentNode_list.ToArray();

          int rbeID = rbeInstance.AddOrGet(cogNodeID, dependenctNode_array);
          //Console.WriteLine($"{rbeID}, Nodes : {string.Join(",", dependenctNode_array)}");
        }

        //Console.WriteLine($"{string.Join(",", cols)}, 열 개수:{cols.Length}");
      }
    }
  }
}
