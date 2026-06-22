using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// CBEAM 양 끝점과 동일한 (indep, dep) 페어를 가진 RBE2 dep를 제거한다.
/// CBEAM이 이미 두 노드를 연결하는데 RBE2가 같은 페어를 강체 결합하면
/// 동일 자유도에 중복 구속이 발생하여 NASTRAN 해석 시 비물리적 거동이 유발된다.
/// NodeEquivalenceStage(final) 직후 / FinalValidationStage 직전에 실행된다.
/// </summary>
public sealed class RbeBeamDeduplicateStage : IPipelineStage
{
    public string Name => "RbeBeamDeduplicate";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;

        var elementEdges = new HashSet<(int, int)>(model.Elements.Count);
        foreach (var elem in model.Elements)
            elementEdges.Add(EdgeKey(elem.StartNodeId, elem.EndNodeId));

        int rbeRemoved = 0;
        int depRemoved = 0;
        var toRemove   = new List<int>();

        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rigid = model.Rigids[i];
            int indep = rigid.IndependentNodeId;

            var keptDeps    = new List<int>(rigid.DependentNodeIds.Count);
            int removedHere = 0;

            foreach (int dep in rigid.DependentNodeIds)
            {
                if (elementEdges.Contains(EdgeKey(indep, dep)))
                {
                    removedHere++;
                    ctx.AddTrace(TraceAction.ElementRemoved, Name,
                        elementId: rigid.Id, nodeId: dep, relatedNodeId: indep,
                        note: $"RBE dep N{dep} ↔ indep N{indep} duplicates CBEAM edge — dep removed");
                    ctx.AddDiagnostic(DiagnosticSeverity.Warning, "RBE2_REDUNDANT_WITH_BEAM",
                        $"RBE {rigid.Id}: dep N{dep} ↔ indep N{indep} duplicates a CBEAM edge; dep removed.",
                        elementId: rigid.Id, nodeId: dep);
                }
                else
                {
                    keptDeps.Add(dep);
                }
            }

            if (removedHere == 0) continue;
            depRemoved += removedHere;

            if (keptDeps.Count == 0)
            {
                // BOX UBOLT 패턴 보존 — NodeEquivalenceStage.IsDeferredBoxUbolt 와 동일
                if (IsDeferredBoxUbolt(rigid))
                {
                    ctx.AddDiagnostic(DiagnosticSeverity.Warning, "BOX_UBOLT_EMPTY_AFTER_DEDUP",
                        $"BOX UBOLT RBE {rigid.Id} has no remaining deps after beam-dedup.",
                        elementId: rigid.Id);
                    continue;
                }

                toRemove.Add(rigid.Id);
                rbeRemoved++;
                ctx.AddTrace(TraceAction.ElementRemoved, Name,
                    elementId: rigid.Id, nodeId: indep,
                    note: "RBE2 emptied after beam-dedup — removed");
                continue;
            }

            // 일부 dep만 제거 — RigidElement 는 immutable 이므로 교체
            model.Rigids[i] = new RigidElement(
                rigid.Id, indep, keptDeps,
                rigid.Remark, rigid.SourceName, rigid.Cm, rigid.DetailRemark);
        }

        if (toRemove.Count > 0)
        {
            var removeSet = toRemove.ToHashSet();
            model.Rigids.RemoveAll(r => removeSet.Contains(r.Id));
        }

        if (depRemoved > 0 || rbeRemoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "RBE_BEAM_DEDUP_SUMMARY",
                $"{depRemoved} dep(s) removed, {rbeRemoved} empty RBE2(s) removed (beam-edge duplicates).");

        return true;
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static bool IsDeferredBoxUbolt(RigidElement rigid)
        => rigid.Remark == "UBOLT" &&
           string.Equals(rigid.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase);
}
