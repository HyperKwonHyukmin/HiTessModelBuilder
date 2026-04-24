namespace Cmb.Core.Model;

[Flags]
public enum NodeTags
{
    None         = 0,
    Weld         = 1,
    Intersection = 2,
    Merged       = 4,
    Boundary     = 8,
}

public enum BeamSectionKind
{
    H,
    L,
    Rod,
    Tube,
    Box,
    Channel,
    Bar,
}

public enum EntityCategory
{
    Structure,
    Pipe,
    Equipment,
}
