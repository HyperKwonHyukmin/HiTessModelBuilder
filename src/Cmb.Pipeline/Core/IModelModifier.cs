using Cmb.Core.Model.Context;

namespace Cmb.Pipeline.Core;

public interface IModelModifier
{
    void Modify(FeModel model, RunOptions options);
}
