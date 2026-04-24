namespace Cmb.Core.Model;

public sealed class PointMass
{
    public int Id { get; }
    public int NodeId { get; }
    public double Mass { get; }
    public string? SourceName { get; }

    public PointMass(int id, int nodeId, double mass, string? sourceName = null)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (nodeId <= 0) throw new ArgumentOutOfRangeException(nameof(nodeId));
        if (mass < 0) throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be non-negative.");

        Id = id;
        NodeId = nodeId;
        Mass = mass;
        SourceName = sourceName;
    }
}
