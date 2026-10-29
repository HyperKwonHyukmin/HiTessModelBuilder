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
        // 유효 DOF(1~6) 만 허용한다. 0/7/8/9 등 범위 밖 성분은 뷰어 IsValidCm 과 마찬가지로 버린다.
        // (범위 밖만 들어와 남는 성분이 없으면 기존 빈 문자열 처리와 동일하게 "123456" 으로 폴백)
        var chars = cm.Where(c => c is >= '1' and <= '6').Distinct().OrderBy(c => c).ToArray();
        return chars.Length == 0 ? "123456" : new string(chars);
    }
}
