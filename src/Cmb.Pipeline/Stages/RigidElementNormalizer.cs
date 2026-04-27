using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

internal static class RigidElementNormalizer
{
    public static int MergeSharedDependentNodes(StageContext ctx, string stageName)
    {
        var model = ctx.Model;
        var depToRigidIds = new Dictionary<int, List<int>>();

        foreach (var r in model.Rigids)
        {
            foreach (int dep in r.DependentNodeIds)
            {
                if (!depToRigidIds.TryGetValue(dep, out var ids))
                {
                    ids = [];
                    depToRigidIds[dep] = ids;
                }
                ids.Add(r.Id);
            }
        }

        int merged = 0;
        foreach (var (sharedDep, rigidIds) in depToRigidIds.Where(kv => kv.Value.Count >= 2).ToList())
        {
            var conflicts = model.Rigids.Where(r => rigidIds.Contains(r.Id)).ToList();
            if (conflicts.Count < 2) continue;

            var newDeps = new List<int>();
            var cmChars = new HashSet<char>();
            bool anyUbolt = false;
            int uboltMaster = -1;
            string remark = "MERGED_SHARED_DEP";
            string? sourceName = null;
            string? detailRemark = null;

            foreach (var r in conflicts)
            {
                newDeps.Add(r.IndependentNodeId);
                newDeps.AddRange(r.DependentNodeIds.Where(d => d != sharedDep));
                foreach (char c in r.Cm.Where(char.IsDigit))
                    cmChars.Add(c);

                if (r.Remark == "UBOLT")
                {
                    anyUbolt = true;
                    if (uboltMaster < 0) uboltMaster = r.IndependentNodeId;
                }

                sourceName ??= r.SourceName;
                detailRemark ??= r.DetailRemark;
            }

            string cm = cmChars.Count == 0
                ? "123456"
                : new string(cmChars.OrderBy(c => c).ToArray());

            int finalMaster = sharedDep;
            int massMaster = newDeps.FirstOrDefault(n => model.PointMasses.Any(pm => pm.NodeId == n));
            if (massMaster > 0)
            {
                finalMaster = massMaster;
                newDeps.RemoveAll(n => n == massMaster);
                newDeps.Add(sharedDep);
            }
            else if (anyUbolt && uboltMaster > 0)
            {
                finalMaster = uboltMaster;
                newDeps.RemoveAll(n => n == uboltMaster);
                newDeps.Add(sharedDep);
                remark = "UBOLT";
            }

            newDeps = newDeps
                .Where(n => n != finalMaster)
                .Distinct()
                .ToList();
            if (newDeps.Count == 0) continue;

            model.Rigids.RemoveAll(r => conflicts.Any(c => c.Id == r.Id));
            int newId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(newId, finalMaster, newDeps, remark, sourceName, cm, detailRemark));
            ctx.AddTrace(TraceAction.ElementCreated, stageName, elementId: newId,
                note: $"merged shared dependent node {sharedDep} from RBE2 {string.Join(",", conflicts.Select(r => r.Id))}");
            merged++;
        }

        if (merged > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "RBE_SHARED_DEP_MERGED",
                $"{merged} shared dependent RBE2 set(s) merged.");

        return merged;
    }
}
