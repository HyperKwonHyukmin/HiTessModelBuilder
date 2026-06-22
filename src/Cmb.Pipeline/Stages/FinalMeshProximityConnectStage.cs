using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// FinalMeshSize 이후 생성된 구조 내부 노드가 작은 고립 구조 그룹과 주변 구조 부재를
/// 실제 접합 후보로 드러내는 경우만 보수적으로 RBE2 연결한다.
/// </summary>
public sealed class FinalMeshProximityConnectStage : IPipelineStage
{
    public string Name => "FinalMeshProximityConnect";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        var tol = ctx.Options.Tolerances;
        model.SyncIdCounters();

        var finalMeshNodeIds = model.TraceLog
            .Where(t => t.StageName == "FinalMeshSize" &&
                        t.Action == TraceAction.NodeCreated &&
                        t.NodeId is not null)
            .Select(t => t.NodeId!.Value)
            .ToHashSet();
        if (finalMeshNodeIds.Count == 0 || model.Elements.Count == 0) return true;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var elemById = model.Elements.ToDictionary(e => e.Id);
        var sectionById = model.Sections.ToDictionary(s => s.Id);
        var components = BuildComponents(model, sectionById);

        var spatial = new ElementSpatialHash(model.Elements, nodeById,
            tol.SpatialCellSizeMm,
            tol.FinalMeshProximityExtraMarginMm + tol.FreeEndRbeMaxSearchMm);

        var rbeEdges = BuildRbeEdges(model);
        var elementEdges = BuildElementEdges(model);
        var pending = new List<(int SourceNodeId, Point3 Projection, int TargetElementId, double Distance)>();

        foreach (int sourceNodeId in finalMeshNodeIds.OrderBy(id => id))
        {
            if (!nodeById.TryGetValue(sourceNodeId, out var sourceNode)) continue;
            if (!components.NodeToComponent.TryGetValue(sourceNodeId, out int sourceCompId)) continue;
            if (!components.ById.TryGetValue(sourceCompId, out var sourceComp)) continue;
            if (!IsEligibleSourceComponent(sourceComp, elemById, tol)) continue;
            if (!TryGetSourceDirection(sourceNodeId, sourceComp.ElementIds, elemById, nodeById, out var sourceDir)) continue;

            double bestDist = double.MaxValue;
            Point3 bestProjection = default;
            int bestTargetId = -1;

            var maxSearch = tol.FreeEndRbeMaxSearchMm + tol.FinalMeshProximityExtraMarginMm;
            var boxMin = new Point3(sourceNode.Position.X - maxSearch, sourceNode.Position.Y - maxSearch, sourceNode.Position.Z - maxSearch);
            var boxMax = new Point3(sourceNode.Position.X + maxSearch, sourceNode.Position.Y + maxSearch, sourceNode.Position.Z + maxSearch);

            foreach (int targetId in spatial.QueryBox(boxMin, boxMax))
            {
                if (!elemById.TryGetValue(targetId, out var target)) continue;
                if (target.Category != EntityCategory.Structure) continue;
                if (sourceComp.ElementIds.Contains(targetId)) continue;
                if (!nodeById.TryGetValue(target.StartNodeId, out var nA)) continue;
                if (!nodeById.TryGetValue(target.EndNodeId, out var nB)) continue;
                if (components.NodeToComponent.GetValueOrDefault(target.StartNodeId) == sourceCompId ||
                    components.NodeToComponent.GetValueOrDefault(target.EndNodeId) == sourceCompId)
                    continue;

                var (projection, t, dist) = ProjectionUtils.ClosestPointOnSegment(sourceNode.Position, nA.Position, nB.Position);
                if (t <= 1e-4 || t >= 1.0 - 1e-4) continue;

                var targetDir = nB.Position - nA.Position;
                if (targetDir.Length <= 1e-9) continue;
                double cosAngle = Math.Abs(Vector3.Dot(sourceDir, targetDir) / (sourceDir.Length * targetDir.Length));
                if (cosAngle > 0.985) continue;

                double sourceDim = sourceComp.MaxSectionDim;
                double targetDim = MaxDim(sectionById, target.PropertyId);
                double allowed = Math.Max(sourceDim, targetDim) + tol.FinalMeshProximityExtraMarginMm;
                if (dist > allowed) continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestProjection = projection;
                    bestTargetId = targetId;
                }
            }

            if (bestTargetId != -1)
                pending.Add((sourceNodeId, bestProjection, bestTargetId, bestDist));
        }

        if (pending.Count == 0) return true;

        var nodeHash = new NodeSpatialHash(model.Nodes, tol.SpatialCellSizeMm);
        int created = 0;
        int skippedExistingEdge = 0;

        foreach (var (sourceNodeId, projection, targetElementId, distance) in pending)
        {
            int projectionNodeId = FindOrCreateNode(ctx, model, nodeById, nodeHash, projection, tol.NodeMergeMm, sourceNodeId, targetElementId);
            if (projectionNodeId == sourceNodeId) continue;

            var edge = EdgeKey(projectionNodeId, sourceNodeId);
            if (elementEdges.Contains(edge) || rbeEdges.Contains(edge))
            {
                skippedExistingEdge++;
                continue;
            }

            int rbeId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(rbeId, projectionNodeId, [sourceNodeId],
                "FINAL_MESH_PROXIMITY"));
            rbeEdges.Add(edge);
            ctx.AddTrace(TraceAction.ElementCreated, Name, elementId: rbeId,
                nodeId: projectionNodeId, relatedNodeId: sourceNodeId,
                relatedElementId: targetElementId,
                note: $"final mesh N{sourceNodeId} → E{targetElementId} ({distance:F1}mm)");
            created++;
        }

        if (created > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FINAL_MESH_PROXIMITY_RBE_CREATED",
                $"{created} final mesh structure node(s) linked to nearby structure element(s).");
        if (skippedExistingEdge > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FINAL_MESH_PROXIMITY_EDGE_SKIPPED",
                $"{skippedExistingEdge} final mesh proximity link(s) skipped because an element/RBE edge already exists.");

        return true;
    }

    private static bool IsEligibleSourceComponent(ComponentInfo comp, Dictionary<int, BeamElement> elemById, Tolerances tol)
    {
        if (comp.ElementIds.Count == 0 || comp.ElementIds.Count > tol.FinalMeshProximityMaxSourceElements)
            return false;

        foreach (int eid in comp.ElementIds)
        {
            if (!elemById.TryGetValue(eid, out var e)) return false;
            if (e.Category != EntityCategory.Structure) return false;
        }

        return true;
    }

    private static bool TryGetSourceDirection(
        int nodeId,
        IReadOnlySet<int> componentElementIds,
        Dictionary<int, BeamElement> elemById,
        Dictionary<int, Node> nodeById,
        out Vector3 direction)
    {
        direction = default;

        foreach (int eid in componentElementIds)
        {
            if (!elemById.TryGetValue(eid, out var elem)) continue;
            if (elem.StartNodeId != nodeId && elem.EndNodeId != nodeId) continue;
            if (!nodeById.TryGetValue(elem.StartNodeId, out var a) ||
                !nodeById.TryGetValue(elem.EndNodeId, out var b)) continue;

            var span = b.Position - a.Position;
            if (span.Length <= 1e-9) continue;
            direction = span;
            return true;
        }

        return false;
    }

    private static double MaxDim(IReadOnlyDictionary<int, BeamSection> sectionById, int propertyId)
        => sectionById.TryGetValue(propertyId, out var sec) ? sec.MaxCrossSectionDim() : 0.0;

    private static int FindOrCreateNode(
        StageContext ctx,
        FeModel model,
        Dictionary<int, Node> nodeById,
        NodeSpatialHash nodeHash,
        Point3 position,
        double mergeTol,
        int sourceNodeId,
        int targetElementId)
    {
        foreach (int id in nodeHash.QueryNear(position, mergeTol))
        {
            if (!nodeById.TryGetValue(id, out var node)) continue;
            if (node.Position.DistanceTo(position) <= mergeTol) return id;
        }

        int nodeId = model.AllocNodeId();
        var newNode = new Node(nodeId, position);
        model.Nodes.Add(newNode);
        nodeById[nodeId] = newNode;
        nodeHash.Add(newNode.Id, newNode.Position);
        ctx.AddTrace(TraceAction.NodeCreated, "FinalMeshProximityConnect",
            nodeId: nodeId, relatedNodeId: sourceNodeId, relatedElementId: targetElementId,
            note: "projection node for final mesh proximity RBE");
        return nodeId;
    }

    private static ComponentIndex BuildComponents(
        FeModel model,
        IReadOnlyDictionary<int, BeamSection> sectionById)
    {
        var adjacency = new Dictionary<int, HashSet<int>>();
        void AddNode(int id)
        {
            if (!adjacency.ContainsKey(id)) adjacency[id] = [];
        }
        void AddEdge(int a, int b)
        {
            AddNode(a);
            AddNode(b);
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        foreach (var n in model.Nodes) AddNode(n.Id);
        foreach (var e in model.Elements) AddEdge(e.StartNodeId, e.EndNodeId);
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                AddEdge(r.IndependentNodeId, dep);

        var nodeToComponent = new Dictionary<int, int>();
        var byId = new Dictionary<int, ComponentInfo>();
        int compId = 0;

        foreach (int start in adjacency.Keys.OrderBy(id => id))
        {
            if (nodeToComponent.ContainsKey(start)) continue;

            var nodes = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            nodeToComponent[start] = compId;

            while (stack.Count > 0)
            {
                int node = stack.Pop();
                nodes.Add(node);
                foreach (int next in adjacency[node])
                {
                    if (nodeToComponent.ContainsKey(next)) continue;
                    nodeToComponent[next] = compId;
                    stack.Push(next);
                }
            }

            byId[compId] = new ComponentInfo(nodes);
            compId++;
        }

        foreach (var e in model.Elements)
        {
            if (!nodeToComponent.TryGetValue(e.StartNodeId, out int id)) continue;
            var comp = byId[id];
            comp.ElementIds.Add(e.Id);
            comp.MaxSectionDim = Math.Max(comp.MaxSectionDim, MaxDim(sectionById, e.PropertyId));
        }

        return new ComponentIndex(nodeToComponent, byId);
    }

    private static HashSet<(int, int)> BuildElementEdges(FeModel model)
        => model.Elements.Select(e => EdgeKey(e.StartNodeId, e.EndNodeId)).ToHashSet();

    private static HashSet<(int, int)> BuildRbeEdges(FeModel model)
    {
        var edges = new HashSet<(int, int)>();
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                edges.Add(EdgeKey(r.IndependentNodeId, dep));
        return edges;
    }

    private static (int, int) EdgeKey(int a, int b)
        => a < b ? (a, b) : (b, a);

    private sealed record ComponentIndex(
        Dictionary<int, int> NodeToComponent,
        Dictionary<int, ComponentInfo> ById);

    private sealed class ComponentInfo(HashSet<int> nodeIds)
    {
        public HashSet<int> NodeIds { get; } = nodeIds;
        public HashSet<int> ElementIds { get; } = [];
        public double MaxSectionDim { get; set; }
    }
}
