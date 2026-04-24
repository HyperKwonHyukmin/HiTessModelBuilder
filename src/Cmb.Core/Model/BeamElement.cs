using Cmb.Core.Geometry;

namespace Cmb.Core.Model;

public sealed class BeamElement
{
    public int Id { get; }
    public int StartNodeId { get; }
    public int EndNodeId { get; }
    public int PropertyId { get; }
    public EntityCategory Category { get; }
    public Vector3 Orientation { get; }
    public string? SourceName { get; }
    public int? ParentElementId { get; }

    public BeamElement(
        int id,
        int startNodeId,
        int endNodeId,
        int propertyId,
        EntityCategory category,
        Vector3 orientation,
        string? sourceName = null,
        int? parentElementId = null)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (startNodeId <= 0) throw new ArgumentOutOfRangeException(nameof(startNodeId));
        if (endNodeId <= 0) throw new ArgumentOutOfRangeException(nameof(endNodeId));
        if (propertyId <= 0) throw new ArgumentOutOfRangeException(nameof(propertyId));

        Id = id;
        StartNodeId = startNodeId;
        EndNodeId = endNodeId;
        PropertyId = propertyId;
        Category = category;
        Orientation = orientation;
        SourceName = sourceName;
        ParentElementId = parentElementId;
    }
}
