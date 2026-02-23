using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Preprocess;
using HiTessModelBuilder.Pipeline.ElementModifier;
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
    private readonly bool _verboseDebug;
    public FeModelProcessPipeline(RawStructureDesignData? rawStructureDesignData, FeModelContext context,
      string CsvFolderPath, string inputFileName, bool pipelineDebug, bool verboseDebug) 
    {
      this._rawStructureDesignData = rawStructureDesignData;
      this._context = context;
      this._csvFolderPath = CsvFolderPath;
      this._inputFileName = inputFileName;
      this._pipelineDebug = pipelineDebug;
      this._verboseDebug = verboseDebug;

    }

    public void Run()
    {
      if (this._pipelineDebug) Console.WriteLine("\n[Pipeline Started] Processing FE Model...");

      ExportBaseline();
      RunStagedPipeline();
    }

    private void ExportBaseline()
    {
      string stageName = "STAGE_00";
      Console.WriteLine($"================ {stageName} =================");
      var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, _verboseDebug);
      BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
    }

    private void RunStagedPipeline()
    {
      RunStage("STAGE_01", () => ElementSplitByExistingNodesRun(_pipelineDebug, _verboseDebug));
    }

    private void RunStage(string stageName, Action action, string customExportName = null)
    {
      Console.WriteLine($"================ {stageName} =================");
      action();
      StructuralSanityInspector.Inspect(_context, _pipelineDebug, _verboseDebug);
    }
    private void ElementSplitByExistingNodesRun(bool pipelineDebug, bool verboseDebug)
    {
      var opt = new ElementSplitByExistingNodesModifier.Options(1.0, 1e-9, 0.05, 1e-6, 5.0, false, true, false, pipelineDebug);
      ElementSplitByExistingNodesModifier.Run(_context, opt, Console.WriteLine);
    }
  }
