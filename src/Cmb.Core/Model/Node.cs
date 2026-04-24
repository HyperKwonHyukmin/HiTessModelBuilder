using Cmb.Core.Geometry;

namespace Cmb.Core.Model;

public sealed class Node
{
    public int Id { get; }
    public Point3 Position { get; }
    public NodeTags Tags { get; private set; }

    public Node(int id, Point3 position, NodeTags tags = NodeTags.None)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Node Id must be positive.");
        Id = id;
        Position = position;
        Tags = tags;
    }

    public void AddTag(NodeTags tag) => Tags |= tag;
    public void RemoveTag(NodeTags tag) => Tags &= ~tag;
    public bool HasTag(NodeTags tag) => (Tags & tag) != 0;
}
