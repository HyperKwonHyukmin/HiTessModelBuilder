using Cmb.Core.Model.Context;

namespace Cmb.Pipeline.Core;

public interface IModelInspector
{
    IReadOnlyList<Diagnostic> Inspect(FeModel model, RunOptions options);
}
