using System.Globalization;
using System.Text;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

internal static class ExcludedRigidAudit
{
    internal static void Write(string outputDir, IEnumerable<Diagnostic> diagnostics, FeModel model)
    {
        var excluded = diagnostics.Where(d => d.Code == "EMPTY_RIGID_EXCLUDED").ToList();
        if (excluded.Count == 0) return;
        using var writer = new StreamWriter(Path.Combine(outputDir, "00_ExcludedRigids.csv"), false, new UTF8Encoding(true));
        writer.WriteLine("code,rigidId,nodeId,inputName,xMm,yMm,zMm,reason");
        foreach (var d in excluded)
        {
            var node = d.NodeId is int id ? model.FindNode(id) : null;
            var name = System.Text.RegularExpressions.Regex.Match(d.Message, @"SourceName=([^;]*);").Groups[1].Value;
            var fields = new[] { d.Code, d.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "",
                d.NodeId?.ToString(CultureInfo.InvariantCulture) ?? "", name,
                node?.Position.X.ToString("R", CultureInfo.InvariantCulture) ?? "",
                node?.Position.Y.ToString("R", CultureInfo.InvariantCulture) ?? "",
                node?.Position.Z.ToString("R", CultureInfo.InvariantCulture) ?? "", d.Message };
            writer.WriteLine(string.Join(",", fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
        }
    }
}
