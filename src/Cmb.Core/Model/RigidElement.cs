namespace Cmb.Core.Model;

public sealed class RigidElement
{
    public int Id { get; }
    public int IndependentNodeId { get; }
    public IReadOnlyList<int> DependentNodeIds { get; }
    public string Remark { get; }
    public string? SourceName { get; }

    public RigidElement(int id, int independentNodeId, IEnumerable<int> dependentNodeIds, string remark = "", string? sourceName = null)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (independentNodeId <= 0) throw new ArgumentOutOfRangeException(nameof(independentNodeId));

        Id = id;
        IndependentNodeId = independentNodeId;
        DependentNodeIds = dependentNodeIds.ToList().AsReadOnly();
        Remark = remark;
        SourceName = sourceName;
    }
}
