namespace Cmb.Pipeline.Core;

public interface IPipelineStage
{
    string Name { get; }

    // Returns true if the stage succeeded; false aborts the pipeline.
    bool Execute(StageContext ctx);
}
