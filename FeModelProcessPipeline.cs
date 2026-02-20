using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Preprocess;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Pipeline
{
  public class FeModelProcessPipeline
  {
    private readonly RawStructureDesignData _rawStructureDesignData;
    private readonly FeModelContext _context;
    private readonly string _inputFileName;
    private readonly string _csvFolderPath;
    private readonly bool _pipelineDebug;
    public FeModelProcessPipeline(RawStructureDesignData? rawStructureDesignData, FeModelContext context,
      string CsvFolderPath, string inputFileName, bool pipelineDebug) 
    {
      this._rawStructureDesignData = rawStructureDesignData;
      this._context = context;
      this._csvFolderPath = CsvFolderPath;
      this._inputFileName = inputFileName;
      this._pipelineDebug = pipelineDebug;

    }

    public void Run()
    {
      if (this._pipelineDebug) Console.WriteLine("\n[Pipeline Started] Processing FE Model...");

      ExportBaseline();
    }

    private void ExportBaseline()
    {
      string stageName = "STAGE_00";
      Console.WriteLine($"================ {stageName} =================");
      StructuralSanityInspector.Inspect(_context, _pipelineDebug);
      BdfExporter.Export(_context, _csvFolderPath, stageName);
    }
  }
}
