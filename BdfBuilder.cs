using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Exporter
{
  public class BdfBuilder
  {
    public int sol;
    public FeModelContext feModelContext;
    int LoadCase;
    public List<int> SpcList = new List<int>();
    //public List<ForceLoad> ForceLoads = new List<ForceLoad>();

    // BDF에 입력된 텍스트 라인모음 리스트
    public List<String> BdfLines = new List<String>();

    // 생성자 함수
    public BdfBuilder(
      int Sol,
      FeModelContext FeModelContext,
      List<int> spcList = null,
      //List<ForceLoad> forceLoads = null, // [추가] 인자
      int loadCase = 1)
    {
      this.sol = Sol;
      this.feModelContext = FeModelContext;
      this.SpcList = spcList ?? new List<int>();
      //this.RigidMap = rigidMap ?? new Dictionary<int, MooringFittingConnectionModifier.RigidInfo>();
      //this.ForceLoads = forceLoads ?? new List<ForceLoad>(); // [추가] 초기화
      this.LoadCase = loadCase;
    }

    public void Run()
    {
      // Nastran 솔버 입력
      ExecutiveControlSection();

      // 출력결과 종류 설정, LoadCase 설정
      CaseControlSection();

      // Subcase 입력
      SubcaseSection();

      // Node, Element 데이터 입력
      NodeElementSection();

      // Property, Material 데이터 입력
      PropertyMaterialSection();

      // RBE 연결
      RigidElementSection();

      // 경계 조건 데이터 입력
      BoundaryConditionSection();

      // 하중 입력
      LoadBulkSection();

      // 종료 시그니쳐
      EndSigniture();

    }

    public void ExecutiveControlSection()
    {
      BdfLines.Add(BdfFormatFields.FormatField($"SOL {this.sol}"));
      BdfLines.Add(BdfFormatFields.FormatField($"CEND"));
    }

    public void CaseControlSection()
    {
      BdfLines.Add("DISPLACEMENT = ALL");
      BdfLines.Add("FORCE = ALL");
      BdfLines.Add("SPCFORCES = ALL");
      BdfLines.Add("STRESS = ALL");
    }

    public void SubcaseSection()
    {
      BdfLines.Add("SUBCASE       1");
      BdfLines.Add("LABEL = LC1");
      BdfLines.Add("SPC = 1");
      BdfLines.Add("LOAD = 2");
      BdfLines.Add("ANALYSIS = STATICS");
      BdfLines.Add("BEGIN BULK");
      BdfLines.Add("PARAM,POST,-1");
    }      

    public void NodeElementSection()
    {
      foreach (var node in this.feModelContext.Nodes)
      {
        string nodeText = $"{BdfFormatFields.FormatField("GRID")}"
          + $"{BdfFormatFields.FormatField(node.Key, "right")}"
          + $"{BdfFormatFields.FormatField("")}"
          + $"{BdfFormatFields.FormatField(node.Value.X, "right")}"
          + $"{BdfFormatFields.FormatField(node.Value.Y, "right")}"
          + $"{BdfFormatFields.FormatField(node.Value.Z, "right")}";
        BdfLines.Add(nodeText);
      }

      foreach (var element in this.feModelContext.Elements)
      {
        // ★ [추가] ExtraData에서 Orientation 정보 꺼내기 (기본값 설정)
        double oriX = 0.0, oriY = 0.0, oriZ = 1.0;

        if (element.Value.ExtraData.TryGetValue("OriX", out string sX))
          double.TryParse(sX, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out oriX);
        if (element.Value.ExtraData.TryGetValue("OriY", out string sY))
          double.TryParse(sY, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out oriY);
        if (element.Value.ExtraData.TryGetValue("OriZ", out string sZ))
          double.TryParse(sZ, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out oriZ);
        string elementText = $"{BdfFormatFields.FormatField("CBEAM")}"
         + $"{BdfFormatFields.FormatField(element.Key, "right")}"
         + $"{BdfFormatFields.FormatField(element.Value.PropertyID, "right")}"
         + $"{BdfFormatFields.FormatField(element.Value.NodeIDs[0], "right")}"
         + $"{BdfFormatFields.FormatField(element.Value.NodeIDs[1], "right")}"
          + $"{BdfFormatFields.FormatField(oriX, "right")}" // X1 (oriX)
         + $"{BdfFormatFields.FormatField(oriY, "right")}" // X2 (oriY)
         + $"{BdfFormatFields.FormatField(oriZ, "right")}" // X3 (oriZ)
         + $"{BdfFormatFields.FormatField("BGG", "right")}";
        BdfLines.Add(elementText);
      }
    }

    public void 
      PropertyMaterialSection()
    {
      foreach (var property in this.feModelContext.Properties)
      {
        string type = property.Value.Type.ToUpper(); // 대소문자 무시
     
        // 1. L
        if (type == "L")
        {
          string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
            + $"{BdfFormatFields.FormatField(property.Key, "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
            + $"{BdfFormatFields.FormatField("", "right")}"
            + $"{BdfFormatFields.FormatField("L", "right")}";
          BdfLines.Add(propertyText);

          propertyText = $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[1], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[2], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[2], "right")}"
            + $"{BdfFormatFields.FormatField("0.0", "right")}"; 

          BdfLines.Add(propertyText);
        }
        // 2. H
        else if (type == "H")
        {
          string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
            + $"{BdfFormatFields.FormatField(property.Key, "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
            + $"{BdfFormatFields.FormatField("", "right")}"
            + $"{BdfFormatFields.FormatField("H", "right")}";
          BdfLines.Add(propertyText);

          propertyText = $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[1], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[2], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[3], "right")}"
            + $"{BdfFormatFields.FormatField("0.0", "right")}";

          BdfLines.Add(propertyText);
        }

        // 3. CHAN
        else if (type == "CHAN")
        {
          string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
            + $"{BdfFormatFields.FormatField(property.Key, "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
            + $"{BdfFormatFields.FormatField("", "right")}"
            + $"{BdfFormatFields.FormatField("CHAN", "right")}";
          BdfLines.Add(propertyText);

          propertyText = $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[1], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[2], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[3], "right")}"
            + $"{BdfFormatFields.FormatField("0.0", "right")}";

          BdfLines.Add(propertyText);
        }

        // 4. BAR
        else if (type == "BAR")
        {
          string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
            + $"{BdfFormatFields.FormatField(property.Key, "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
            + $"{BdfFormatFields.FormatField("", "right")}"
            + $"{BdfFormatFields.FormatField("BAR", "right")}";
          BdfLines.Add(propertyText);

          propertyText = $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[1], "right")}"
            + $"{BdfFormatFields.FormatField("0.0", "right")}";

          BdfLines.Add(propertyText);
        }

        // 5. ROD
        else if (type == "ROD")
        {
          string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
            + $"{BdfFormatFields.FormatField(property.Key, "right")}"
            + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
            + $"{BdfFormatFields.FormatField("", "right")}"
            + $"{BdfFormatFields.FormatField("ROD", "right")}";
          BdfLines.Add(propertyText);

          propertyText = $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
            + $"{BdfFormatFields.FormatField("0.0", "right")}";

          BdfLines.Add(propertyText);
        }      

        // 5. BAR
        else if (type == "TUBE")
      {
        string propertyText = $"{BdfFormatFields.FormatField("PBEAML")}"
          + $"{BdfFormatFields.FormatField(property.Key, "right")}"
          + $"{BdfFormatFields.FormatField(property.Value.MaterialID, "right")}"
          + $"{BdfFormatFields.FormatField("", "right")}"
          + $"{BdfFormatFields.FormatField("TUBE", "right")}";
        BdfLines.Add(propertyText);

        propertyText = $"{BdfFormatFields.FormatField("")}"
          + $"{BdfFormatFields.FormatField(property.Value.Dim[0], "right")}"
          + $"{BdfFormatFields.FormatField(property.Value.Dim[1], "right")}"
          + $"{BdfFormatFields.FormatField("0.0", "right")}";

        BdfLines.Add(propertyText);
      }
    }

      // Material 출력
      foreach (var material in this.feModelContext.Materials)
      {
        string materialText = $"{BdfFormatFields.FormatField("MAT1")}"
            + $"{BdfFormatFields.FormatField(material.Key, "right")}"
            + $"{BdfFormatFields.FormatField(material.Value.E, "right")}"
            + $"{BdfFormatFields.FormatField("")}"
            + $"{BdfFormatFields.FormatField(material.Value.Nu, "right")}"
            + $"{BdfFormatFields.FormatField(material.Value.Rho, "right", true)}";

        BdfLines.Add(materialText);
      }
    }

    public void RigidElementSection()
    {
      if (this.feModelContext.Rigids == null || this.feModelContext.Rigids.Count == 0) return;

      foreach (var kv in this.feModelContext.Rigids)
      {
        int eid = kv.Key;      // 9000001 부터 시작하는 고유 ID
        var info = kv.Value;

        // Dependent Node가 없으면 RBE2 생성 불가하므로 스킵
        if (info.DependentNodeIDs == null || info.DependentNodeIDs.Count == 0) continue;

        // -----------------------------------------------------------------------
        // Nastran RBE2 Format Logic (Small Field)
        // Row 1: [RBE2][EID][GN][CM][GM1][GM2][GM3][GM4][GM5][+]
        // Row 2: [+][GM6][GM7]...
        // -----------------------------------------------------------------------
        var sb = new StringBuilder();

        // 1. 첫 번째 줄 헤더 작성 (필드 1~4 사용)
        sb.Append(BdfFormatFields.FormatField("RBE2"));
        sb.Append(BdfFormatFields.FormatField(eid, "right"));
        sb.Append(BdfFormatFields.FormatField(info.IndependentNodeID, "right"));
        sb.Append(BdfFormatFields.FormatField(info.Cm, "right")); // DOF 고정 (123456)

        // 현재 줄에서 사용된 필드 수 (1~4번 필드 사용됨)
        int fieldsUsed = 4;

        // 2. Dependent Nodes (GMi) 순회
        for (int i = 0; i < info.DependentNodeIDs.Count; i++)
        {
          int depNodeID = info.DependentNodeIDs[i];

          // 필드 9번까지 꽉 찼다면 줄바꿈 처리 (필드 10은 연속 마크용)
          if (fieldsUsed >= 9)
          {
            sb.Append(BdfFormatFields.FormatField("+")); // 필드 10: Continuation Mark
            BdfLines.Add(sb.ToString());

            // StringBuilder 리셋 및 다음 줄 초기화
            sb.Clear();
            sb.Append(BdfFormatFields.FormatField("+")); // 다음 줄 필드 1: Continuation Mark Match
            fieldsUsed = 1; // 필드 1 사용됨
          }

          // 노드 ID 추가
          sb.Append(BdfFormatFields.FormatField(depNodeID, "right"));
          fieldsUsed++;
        }

        // 마지막 줄이 남아있다면 리스트에 추가
        if (sb.Length > 0)
        {
          BdfLines.Add(sb.ToString());
        }
      }
    }

    public void BoundaryConditionSection()
    {
      // 리스트가 비어있거나 null이면 작성하지 않음
      if (this.SpcList == null || this.SpcList.Count == 0) return;

      foreach (int nodeId in this.SpcList)
      {
        // ---------------------------------------------------------
        // Nastran SPC Card Format (Small Field)
        // 1. Keyword : "SPC"
        // 2. SID     : Set ID (1로 고정)
        // 3. G       : Grid ID (Node ID)
        // 4. C       : Component (자유도, 123456 고정)
        // 5. D       : Enforced Displacement (0.0 고정)
        // ---------------------------------------------------------

        string spcLine = $"{BdfFormatFields.FormatField("SPC")}"
                       + $"{BdfFormatFields.FormatField(1, "right")}"          // SID = 1
                       + $"{BdfFormatFields.FormatField(nodeId, "right")}"     // Node ID
                       + $"{BdfFormatFields.FormatField("123456", "right")}"  // DOF
                       + $"{BdfFormatFields.FormatField(0.0, "right")}";      // Value

        BdfLines.Add(spcLine);
      }
    }

    public void LoadBulkSection()
    {
      BdfLines.Add("GRAV           2          9800.0     0.0     0.0    -1.0 ");
    }
    //public void LoadBulkSection()
    //{
    //  if (this.ForceLoads == null || this.ForceLoads.Count == 0) return;
    //  foreach (var load in this.ForceLoads)
    //  {
    //    // FORCE Card Format:
    //    // FORCE, SID, G, CID, F, N1, N2, N3
    //    // SID: Load Case ID
    //    // G: Grid ID
    //    // CID: Coord System (0 = Basic)
    //    // F: Scale Factor (1.0으로 두고 N1~N3에 실제 힘 성분 입력)
    //    // N1, N2, N3: Vector components


    //    string line = $"{BdfFormatFields.FormatField("FORCE")}" +
    //                  $"{BdfFormatFields.FormatField(load.LoadCaseID, "right")}" +
    //                  $"{BdfFormatFields.FormatField(load.NodeID, "right")}" +
    //                  $"{BdfFormatFields.FormatField(0, "right")}" +     // CID=0
    //                  $"{BdfFormatFields.FormatField(1.0, "right")}" +   // F=1.0 (Scale)
    //                  $"{BdfFormatFields.FormatNastranField(load.Vector.X)}" +
    //                  $"{BdfFormatFields.FormatNastranField(load.Vector.Y)}" +
    //                  $"{BdfFormatFields.FormatNastranField(load.Vector.Z)}";

    //    BdfLines.Add(line);
    //  }
    //}

    public void EndSigniture()
    {
      BdfLines.Add("ENDDATA");
    }
  }
}
