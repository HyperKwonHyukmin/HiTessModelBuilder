using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Services.Builders
{
  public class RawFeModelBuilder
  {
    public RawStructureDesignData _rawStructureDesignData;
    public FeModelContext _feModelContext;
    bool _debugPrint;

    public RawFeModelBuilder(RawStructureDesignData rawStructureDesignData,
      FeModelContext feModelContext, bool debugPrint = false)
    {
      this._rawStructureDesignData = rawStructureDesignData;
      this._feModelContext = feModelContext;
      this._debugPrint = debugPrint;
    }

    public void Build()
    {
      // material을 Steel 하나 이기에 생성
      int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

      // angle FE 모델 생성
      foreach(var ang in _rawStructureDesignData.AngDesignList)
      {    
        double[] inputDim = new double[4] { ang.Dim1, ang.Dim2, ang.Dim3, ang.Dim3 };

        // property 생성
        int propertyID = _feModelContext.Properties.AddOrGet("L", inputDim, materialID);

        // node 생성
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(ang.Poss[0], ang.Poss[1], ang.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(ang.Pose[0], ang.Pose[1], ang.Pose[2]);

        // 추가 정보 입력
        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["RawType"] = "ANGLE"; // 원래 부재 형태
        extraData["FeType"] = "I"; // FE에 반영하는 부재 형태
        extraData["ID"] = ang.Name; // 부재 고유 ID

        // element 생성
        int eleID = _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID },
          propertyID, extraData);
      }

      // beam FE 모델 생성
      foreach (var beam in _rawStructureDesignData.BeamDesignList)
      {        
        double[] inputDim = new double[4] { beam.Dim1, beam.Dim2, beam.Dim3, beam.Dim4 };

        // property 생성
        int propertyID = _feModelContext.Properties.AddOrGet("H", inputDim, materialID);

        // node 생성
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(beam.Poss[0], beam.Poss[1], beam.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(beam.Pose[0], beam.Pose[1], beam.Pose[2]);

        // 추가 정보 입력
        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["RawType"] = "BEAM"; // 원래 부재 형태
        extraData["FeType"] = "H"; // FE에 반영하는 부재 형태
        extraData["ID"] = beam.Name; // 부재 고유 ID

        // element 생성
        int eleID = _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID },
          propertyID, extraData);
      }

      // Channel FE 모델 생성
      foreach (var bsc in _rawStructureDesignData.BscDesignList)
      {
        //Console.WriteLine(bsc);
        //Console.WriteLine($"{bsc.Dim1}, {bsc.Dim2}, {bsc.Dim3}, {bsc.Dim4}");
        double[] inputDim = new double[4] { bsc.Dim1, bsc.Dim2, bsc.Dim3, bsc.Dim4 };

        // property 생성
        int propertyID = _feModelContext.Properties.AddOrGet("CHAN", inputDim, materialID);

        // node 생성
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(bsc.Poss[0], bsc.Poss[1], bsc.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(bsc.Pose[0], bsc.Pose[1], bsc.Pose[2]);

        // 추가 정보 입력
        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["RawType"] = "BSC"; // 원래 부재 형태
        extraData["FeType"] = "CHAN"; // FE에 반영하는 부재 형태
        extraData["ID"] = bsc.Name; // 부재 고유 ID

        // element 생성
        int eleID = _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID },
          propertyID, extraData);
      }

      // Bulb FE 모델 생성
      foreach (var bulb in _rawStructureDesignData.BulbDesignList)
      {
        Console.WriteLine(bulb);
        Console.WriteLine($"{bulb.Dim1}, {bulb.Dim2}");
        double[] inputDim = new double[2] { bulb.Dim1, bulb.Dim2 };

        // property 생성
        int propertyID = _feModelContext.Properties.AddOrGet("BAR", inputDim, materialID);

        // node 생성
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(bulb.Poss[0], bulb.Poss[1], bulb.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(bulb.Pose[0], bulb.Pose[1], bulb.Pose[2]);

        // 추가 정보 입력
        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["RawType"] = "BULB"; // 원래 부재 형태
        extraData["FeType"] = "BAR"; // FE에 반영하는 부재 형태
        extraData["ID"] = bulb.Name; // 부재 고유 ID

        // element 생성
        int eleID = _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID },
          propertyID, extraData);
      }

      // Rbar FE 모델 생성
      foreach (var rbar in _rawStructureDesignData.RbarDesignList)
      {
        Console.WriteLine(rbar);
        Console.WriteLine($"{rbar.Dim1}");
        double[] inputDim = new double[1] { rbar.Dim1};

        // property 생성
        int propertyID = _feModelContext.Properties.AddOrGet("ROD", inputDim, materialID);

        // node 생성
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(rbar.Poss[0], rbar.Poss[1], rbar.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(rbar.Pose[0], rbar.Pose[1], rbar.Pose[2]);

        // 추가 정보 입력
        Dictionary<string, string> extraData = new Dictionary<string, string>();
        extraData["RawType"] = "RBAR"; // 원래 부재 형태
        extraData["FeType"] = "ROD"; // FE에 반영하는 부재 형태
        extraData["ID"] = rbar.Name; // 부재 고유 ID

        // element 생성
        int eleID = _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID },
          propertyID, extraData);
      }
    }
  }
}
