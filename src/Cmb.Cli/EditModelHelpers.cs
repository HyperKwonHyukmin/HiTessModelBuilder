// M-1 리팩터: Program.cs 에서 분리한 편집용 ID/CM 헬퍼. 동작 불변.
using Cmb.Core.Model.Context;

internal static class EditModelHelpers
{
    internal static int NextUnusedElementId(FeModel model)
    {
        var used = model.Elements.Select(e => e.Id)
            .Concat(model.Rigids.Select(r => r.Id))
            .Concat(model.PointMasses.Select(pm => pm.Id))
            .ToHashSet();
        for (int id = 1; id < int.MaxValue; id++)
            if (!used.Contains(id)) return id;
        throw new InvalidOperationException("No unused element ID available.");
    }
    
    internal static bool IsValidCm(string cm)
    {
        var seen = new HashSet<char>();
        foreach (char ch in cm)
        {
            if (ch is < '1' or > '6') return false;
            if (!seen.Add(ch)) return false;
        }
        return cm.Length is >= 1 and <= 6;
    }
}
