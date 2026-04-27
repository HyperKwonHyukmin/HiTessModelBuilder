namespace Cmb.Core.Model;

public sealed class RigidElement
{
    public int Id { get; }
    public int IndependentNodeId { get; }
    public IReadOnlyList<int> DependentNodeIds { get; }
    public string Remark { get; }
    public string? SourceName { get; }
    public string Cm { get; }
    public string? DetailRemark { get; }

    public RigidElement(
        int id,
        int independentNodeId,
        IEnumerable<int> dependentNodeIds,
        string remark = "",
        string? sourceName = null,
        string cm = "123456",
        string? detailRemark = null)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (independentNodeId <= 0) throw new ArgumentOutOfRangeException(nameof(independentNodeId));

        Id = id;
        IndependentNodeId = independentNodeId;
        DependentNodeIds = dependentNodeIds
            .Where(n => n != independentNodeId)
            .Distinct()
            .ToList()
            .AsReadOnly();
        Remark = remark;
        SourceName = sourceName;
        Cm = NormalizeCm(cm);
        DetailRemark = string.IsNullOrWhiteSpace(detailRemark) ? null : detailRemark;
    }

    private static string NormalizeCm(string? cm)
    {
        if (string.IsNullOrWhiteSpace(cm)) return "123456";
        var chars = cm.Where(char.IsDigit).Distinct().OrderBy(c => c).ToArray();
        return chars.Length == 0 ? "123456" : new string(chars);
    }
}
