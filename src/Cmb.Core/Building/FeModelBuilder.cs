using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Model.Raw;

namespace Cmb.Core.Building;

public sealed class FeModelBuilder
{
    public FeModel Build(RawDesignData data, int pipePidBase = 1000)
    {
        if (pipePidBase < 2)
            throw new ArgumentOutOfRangeException(nameof(pipePidBase), pipePidBase,
                "pipePidBase must be >= 2 to leave room for at least one Structure section.");

        var model     = new FeModel();
        var nodeAlloc = new IdAllocator(1);
        var elemAlloc = new IdAllocator(1); // CBEAM, CONM2, RBE2 공유 ID 공간

        // Structure / Pipe section ID 공간을 분리한다.
        // Structure: [1, pipePidBase)  /  Pipe: [pipePidBase, ...)
        // BDF만 보고도 PID 범위로 카테고리를 구분할 수 있도록.
        var structSecAlloc = new IdAllocator(1);
        var pipeSecAlloc   = new IdAllocator(pipePidBase);

        var nodeMap       = new Dictionary<(long, long, long), int>();
        var nodeById      = new Dictionary<int, Node>();
        var structSecMap  = new Dictionary<string, int>();
        var pipeSecMap    = new Dictionary<string, int>();
        var matMap        = new Dictionary<string, int>();

        model.Materials.Add(Material.DefaultSteel);
        matMap[MaterialKey(Material.DefaultSteel.Name, Material.DefaultSteel.Rho)] = Material.DefaultSteel.Id;
        int matId = model.Materials[0].Id;

        BuildStructure(data.Beams,  model, nodeAlloc, elemAlloc, structSecAlloc, pipePidBase,
                       nodeMap, nodeById, structSecMap, matId);
        BuildPipes    (data.Pipes,  model, nodeAlloc, elemAlloc, pipeSecAlloc,
                       nodeMap, nodeById, pipeSecMap, matMap);
        BuildEquips   (data.Equips, model, nodeAlloc, elemAlloc, nodeMap, nodeById);

        model.SyncIdCounters();
        return model;
    }

    // ── Node helpers ──────────────────────────────────────────────────────────

    private static int GetOrAddNode(
        FeModel model, IdAllocator alloc,
        Dictionary<(long, long, long), int> posMap,
        Dictionary<int, Node> byId,
        double x, double y, double z)
    {
        var key = (Quant(x), Quant(y), Quant(z));
        if (posMap.TryGetValue(key, out int id)) return id;
        id = alloc.Next();
        var node = new Node(id, new Point3(x, y, z));
        model.Nodes.Add(node);
        posMap[key] = id;
        byId[id]    = node;
        model.AddTrace(TraceAction.NodeCreated, "initial", nodeId: id);
        return id;
    }

    private static long Quant(double v) => (long)Math.Round(v * 1000.0);

    // ── Section helpers ───────────────────────────────────────────────────────

    private static int GetOrAddSection(
        FeModel model, IdAllocator alloc,
        Dictionary<string, int> map,
        BeamSectionKind kind, double[] dims, int matId)
    {
        var key = $"{kind}|MAT:{matId}|{string.Join("|", dims.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}";
        if (map.TryGetValue(key, out int id)) return id;
        id = alloc.Next();
        model.Sections.Add(new BeamSection(id, kind, dims, matId));
        map[key] = id;
        return id;
    }

    private static int GetOrAddMaterial(
        FeModel model,
        Dictionary<string, int> map,
        string name,
        double rho)
    {
        var key = MaterialKey(name, rho);
        if (map.TryGetValue(key, out int id)) return id;

        id = model.Materials.Count == 0 ? 1 : model.Materials.Max(m => m.Id) + 1;
        model.Materials.Add(new Material(id, name, Material.DefaultSteel.E, Material.DefaultSteel.Nu, rho));
        map[key] = id;
        return id;
    }

    private static string MaterialKey(string name, double rho) =>
        $"{name}|{rho.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

    private static int GetOrAddPipeMaterial(
        FeModel model,
        Dictionary<string, int> matMap,
        double rOut,
        double rIn)
    {
        const double steelDensity = 7.85e-9; // tonne/mm^3
        const double waterDensity = 1.0e-9;  // tonne/mm^3

        if (rOut <= rIn || rIn <= 0)
            return GetOrAddMaterial(model, matMap, "Steel", steelDensity);

        double steelArea = (rOut * rOut) - (rIn * rIn);
        double fluidArea = rIn * rIn;
        double equivalentDensity =
            ((steelArea * steelDensity) + (fluidArea * waterDensity)) / steelArea;

        string name = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Steel_Fluid_R{rOut:F1}x{rIn:F1}");

        return GetOrAddMaterial(model, matMap, name, equivalentDensity);
    }

    // PBEAML dims:
    //   H    [H, BF, TW, TF]   → [H−2TF, 2TF, BF, TW]
    //   L    [B, D, T]          → [B, D, T, T]
    //   TUBE [OD, t]            → [R_out, R_in]
    //   ROD  [D]                → [R]
    private static double[] NormalizeDims(BeamSectionKind kind, double[] dims) => kind switch
    {
        BeamSectionKind.H    when dims.Length == 4 => [dims[0] - 2.0*dims[3], 2.0*dims[3], dims[1], dims[2]],
        BeamSectionKind.L    when dims.Length == 3 => [dims[0], dims[1], dims[2], dims[2]],
        BeamSectionKind.Tube when dims.Length == 2 => [dims[0] / 2.0, dims[0] / 2.0 - dims[1]],
        BeamSectionKind.Rod  when dims.Length == 1 => [dims[0] / 2.0],
        _ => dims,
    };

    // ── Structure ─────────────────────────────────────────────────────────────

    private static void BuildStructure(
        IReadOnlyList<RawBeamRow> rows,
        FeModel model,
        IdAllocator nodeAlloc, IdAllocator elemAlloc, IdAllocator secAlloc,
        int pipePidBase,
        Dictionary<(long, long, long), int> nodeMap,
        Dictionary<int, Node> nodeById,
        Dictionary<string, int> secMap, int matId)
    {
        foreach (var row in rows)
        {
            if (row.StartPos.Length < 3 || row.EndPos.Length < 3) continue;
            if (MapSectionKind(row.SectionType) is not { } kind) continue;

            var dims  = NormalizeDims(kind, row.Dims);
            int secId = GetOrAddSection(model, secAlloc, secMap, kind, dims, matId);

            if (secId >= pipePidBase)
                throw new InvalidOperationException(
                    $"Structure section count exceeded PipePidBase ({pipePidBase}). " +
                    $"Increase RunOptions.PipePidBase to allow more Structure PBEAML cards.");

            int nA = GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, row.StartPos[0], row.StartPos[1], row.StartPos[2]);
            int nB = GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, row.EndPos[0],   row.EndPos[1],   row.EndPos[2]);
            if (nA == nB) continue;

            var ori = row.Ori.Length >= 3 ? new Vector3(row.Ori[0], row.Ori[1], row.Ori[2]) : Vector3.UnitZ;

            if (!string.IsNullOrEmpty(row.Weld))
            {
                var w = row.Weld.ToLowerInvariant();
                if (w == "start" && nodeById.TryGetValue(nA, out var na)) na.AddTag(NodeTags.Weld);
                if (w == "end"   && nodeById.TryGetValue(nB, out var nb)) nb.AddTag(NodeTags.Weld);
            }

            var elem = new BeamElement(elemAlloc.Next(), nA, nB, secId, EntityCategory.Structure, ori, sourceName: row.Name);
            model.Elements.Add(elem);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: elem.Id, note: row.Name);
        }
    }

    // ── Pipes ─────────────────────────────────────────────────────────────────

    private static void BuildPipes(
        IReadOnlyList<RawPipeRow> rows,
        FeModel model,
        IdAllocator nodeAlloc, IdAllocator elemAlloc, IdAllocator secAlloc,
        Dictionary<(long, long, long), int> nodeMap,
        Dictionary<int, Node> nodeById,
        Dictionary<string, int> secMap,
        Dictionary<string, int> matMap)
    {
        // ── 로컬 헬퍼 (외부 컨텍스트 캡처) ───────────────────────────────────

        int Node(double x, double y, double z) =>
            GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, x, y, z);

        int Section(double outDia, double thick)
        {
            var dims = NormalizeDims(BeamSectionKind.Tube, [outDia, thick]);
            int materialId = GetOrAddPipeMaterial(model, matMap, dims[0], dims[1]);
            return GetOrAddSection(model, secAlloc, secMap, BeamSectionKind.Tube, dims, materialId);
        }

        void PipeElem(int nA, int nB, int secId, string name)
        {
            if (nA == nB) return;
            var elem = new BeamElement(elemAlloc.Next(), nA, nB, secId, EntityCategory.Pipe, Vector3.UnitZ, sourceName: name);
            model.Elements.Add(elem);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: elem.Id, note: name);
        }

        // CSV mass 컬럼은 kg, Nastran [mm, t, s, N] 일관 단위계에서 CONM2는 tonne 입력 → kg→t 변환 (×0.001).
        // PointMass.Mass 는 모델 전반에 걸쳐 항상 tonne 으로 통일.
        void AddMass(double x, double y, double z, double massKg, string name)
        {
            int n = Node(x, y, z);
            double massTon = massKg * 0.001;
            var pm = new PointMass(elemAlloc.Next(), n, massTon, sourceName: name);
            model.PointMasses.Add(pm);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: pm.Id, note: name);
        }

        // ── 타입별 처리 ───────────────────────────────────────────────────────

        foreach (var row in rows)
        {
            var t = row.Type.ToUpperInvariant();

            // UBOLT: RBE2 마커만 생성 (UboltRbeStage가 구조체에 snap).
            // 배관 유체/자중은 pipe CBEAM 등가 밀도에 반영하므로 UBOLT mass 컬럼을 CONM2로 중복 반영하지 않는다.
            if (t == "UBOLT")
            {
                if (row.Pos.Length >= 3)
                {
                    int n = Node(row.Pos[0], row.Pos[1], row.Pos[2]);
                    var rbe = new RigidElement(elemAlloc.Next(), n, [], remark: "UBOLT",
                        sourceName: row.Name, cm: row.Rest ?? "123456", detailRemark: row.Remark);
                    model.Rigids.Add(rbe);
                    model.AddTrace(TraceAction.ElementCreated, "initial", elementId: rbe.Id, note: row.Name);
                }
                continue;
            }

            // ATTA: HiTess와 동일하게 대형 부착물만 PointMass로 반영.
            if (t == "ATTA")
            {
                if (row.Mass * 0.001 > 10.0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            // 인라인 장비 (밸브/트랩/필터 등): center GN → pipe ends GM RBE2
            if (t is "VALV" or "TRAP" or "FILT" or "EXP")
            {
                if (row.Pos.Length >= 3 && row.APos.Length >= 3 && row.LPos.Length >= 3)
                {
                    int center = Node(row.Pos[0], row.Pos[1], row.Pos[2]);
                    int start  = Node(row.APos[0], row.APos[1], row.APos[2]);
                    int end    = Node(row.LPos[0], row.LPos[1], row.LPos[2]);
                    if (start != end)
                    {
                        var rbe = new RigidElement(elemAlloc.Next(), center, [start, end],
                            remark: t, sourceName: row.Name);
                        model.Rigids.Add(rbe);
                        model.AddTrace(TraceAction.ElementCreated, "initial", elementId: rbe.Id, note: row.Name);
                    }
                }
                if (row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            // VTWA: center GN → A/L/P3 GM RBE2
            if (t == "VTWA")
            {
                if (row.Pos.Length >= 3 && row.APos.Length >= 3 && row.LPos.Length >= 3)
                {
                    int center = Node(row.Pos[0], row.Pos[1], row.Pos[2]);
                    var deps = new List<int>
                    {
                        Node(row.APos[0], row.APos[1], row.APos[2]),
                        Node(row.LPos[0], row.LPos[1], row.LPos[2])
                    };
                    if (row.P3Pos is { Length: >= 3 })
                        deps.Add(Node(row.P3Pos[0], row.P3Pos[1], row.P3Pos[2]));

                    var rbe = new RigidElement(elemAlloc.Next(), center, deps,
                        remark: t, sourceName: row.Name);
                    model.Rigids.Add(rbe);
                    model.AddTrace(TraceAction.ElementCreated, "initial", elementId: rbe.Id, note: row.Name);
                }
                if (row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            // OutDia ≤ 0: 단면 정보 없음 → PointMass로 대체
            if (row.OutDia <= 0)
            {
                if (!IsLinePipeType(t) && row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            if (row.APos.Length < 3 || row.LPos.Length < 3) continue;

            int nA    = Node(row.APos[0], row.APos[1], row.APos[2]);
            int nL    = Node(row.LPos[0], row.LPos[1], row.LPos[2]);
            int secId = Section(row.OutDia, row.Thick);

            switch (t)
            {
                // ── 직선 다중절점 파이프 (TUBI) ──────────────────────────────
                case "TUBI":
                {
                    var chain = new List<int> { nA };
                    if (row.InterPos is { Length: >= 3 })
                    {
                        for (int i = 0; i + 2 < row.InterPos.Length; i += 3)
                            chain.Add(Node(row.InterPos[i], row.InterPos[i+1], row.InterPos[i+2]));
                    }
                    chain.Add(nL);
                    for (int i = 0; i < chain.Count - 1; i++)
                        PipeElem(chain[i], chain[i+1], secId, row.Name);
                    break;
                }

                // ── 곡선 파이프 (ELBO / BEND): InterPos 호 → Pos 꺾임점 폴백 ─
                case "ELBO":
                case "BEND":
                {
                    var chain = new List<int> { nA };
                    if (row.InterPos is { Length: >= 3 })
                    {
                        // CSV 제공 호 중간절점으로 폴리라인 생성
                        for (int i = 0; i + 2 < row.InterPos.Length; i += 3)
                            chain.Add(Node(row.InterPos[i], row.InterPos[i+1], row.InterPos[i+2]));
                    }
                    else if (row.Pos.Length >= 3)
                    {
                        // InterPos 없을 때: Pos를 단일 꺾임 절점으로 사용
                        chain.Add(Node(row.Pos[0], row.Pos[1], row.Pos[2]));
                    }
                    chain.Add(nL);
                    for (int i = 0; i < chain.Count - 1; i++)
                        PipeElem(chain[i], chain[i+1], secId, row.Name);
                    break;
                }

                // ── TEE: 메인관 중심에서 분기 ────────────────────────────────
                case "TEE":
                {
                    if (row.Pos.Length >= 3)
                    {
                        int nCenter = Node(row.Pos[0], row.Pos[1], row.Pos[2]);
                        PipeElem(nA, nCenter, secId, row.Name);
                        PipeElem(nCenter, nL, secId, row.Name);

                        if (row.P3Pos is { Length: >= 3 })
                        {
                            double od2   = row.OutDia2 > 0 ? row.OutDia2 : row.OutDia;
                            double thk2  = row.Thick2  > 0 ? row.Thick2  : row.Thick;
                            int secBranch = Section(od2, thk2);
                            int nBranch   = Node(row.P3Pos[0], row.P3Pos[1], row.P3Pos[2]);
                            PipeElem(nCenter, nBranch, secBranch, row.Name);
                        }
                    }
                    else
                    {
                        // Pos 없으면 단순 직선 폴백
                        PipeElem(nA, nL, secId, row.Name);
                    }
                    break;
                }

                // ── FLAN: 질량 포함 플랜지 (질량 처리 여기서 완료) ───────────
                case "FLAN":
                {
                    PipeElem(nA, nL, secId, row.Name);
                    if (row.Mass > 0 && row.Pos.Length >= 3)
                        AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                    continue; // 하단 공통 질량 처리 건너뜀
                }

                // ── 단순 단일 구간 (OLET / REDU / COUP / 기타) ──────────────
                default:
                    PipeElem(nA, nL, secId, row.Name);
                    break;
            }

            // 일반 배관/피팅의 유체 중량은 pipe material 등가 밀도에 포함한다.
            // 별도 PointMass는 FLAN/VALV/TRAP/FILT/EXP/VTWA 및 대형 ATTA 분기에서만 생성한다.
        }
    }

    private static bool IsLinePipeType(string type) =>
        type is "TUBI" or "ELBO" or "BEND" or "OLET" or "REDU" or "COUP" or "TEE";

    // ── Equipment ─────────────────────────────────────────────────────────────

    private static void BuildEquips(
        IReadOnlyList<RawEquipRow> rows,
        FeModel model,
        IdAllocator nodeAlloc, IdAllocator elemAlloc,
        Dictionary<(long, long, long), int> nodeMap,
        Dictionary<int, Node> nodeById)
    {
        const double mountToleranceMm = 10.0;

        var validNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Distinct()
            .ToArray();

        foreach (var row in rows)
        {
            double operatingMass = row.Mass + row.Wvol;
            if (operatingMass <= 0 || row.Cog.Length < 3) continue;

            // CSV equip mass 는 kg, Nastran 일관 단위계에서 tonne 으로 변환 (×0.001).
            double massTon = operatingMass * 0.001;

            if (row.InterPos is not { Length: >= 3 })
            {
                int targetNode = FindClosestValidNode(row.Cog, validNodeIds, nodeById, mountToleranceMm);
                if (targetNode == -1) continue;

                var pointMass = new PointMass(elemAlloc.Next(), targetNode, massTon, sourceName: row.Name);
                model.PointMasses.Add(pointMass);
                model.AddTrace(TraceAction.ElementCreated, "initial", elementId: pointMass.Id,
                    nodeId: targetNode, note: row.Name);
                continue;
            }

            var dependentNodes = new HashSet<int>();
            for (int i = 0; i + 2 < row.InterPos.Length; i += 3)
            {
                int dep = FindClosestValidNode(
                    row.InterPos[i], row.InterPos[i + 1], row.InterPos[i + 2],
                    validNodeIds, nodeById, mountToleranceMm);
                if (dep != -1) dependentNodes.Add(dep);
            }

            if (dependentNodes.Count == 0) continue;

            int n = GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, row.Cog[0], row.Cog[1], row.Cog[2]);
            var equipMass = new PointMass(elemAlloc.Next(), n, massTon, sourceName: row.Name);
            model.PointMasses.Add(equipMass);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: equipMass.Id,
                nodeId: n, note: row.Name);

            var equipRbe = new RigidElement(elemAlloc.Next(), n, dependentNodes,
                remark: "EQUIPMENT", sourceName: row.Name, cm: "123456");
            if (equipRbe.DependentNodeIds.Count == 0) continue;

            model.Rigids.Add(equipRbe);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: equipRbe.Id,
                nodeId: n, note: row.Name);
        }
    }

    private static int FindClosestValidNode(
        double[] pos,
        IEnumerable<int> validNodeIds,
        IReadOnlyDictionary<int, Node> nodeById,
        double tolerance)
        => pos.Length < 3
            ? -1
            : FindClosestValidNode(pos[0], pos[1], pos[2], validNodeIds, nodeById, tolerance);

    private static int FindClosestValidNode(
        double x, double y, double z,
        IEnumerable<int> validNodeIds,
        IReadOnlyDictionary<int, Node> nodeById,
        double tolerance)
    {
        double maxSq = tolerance * tolerance;
        int bestId = -1;
        double bestSq = maxSq;

        foreach (int id in validNodeIds)
        {
            if (!nodeById.TryGetValue(id, out var node)) continue;
            double dx = node.Position.X - x;
            double dy = node.Position.Y - y;
            double dz = node.Position.Z - z;
            double sq = dx * dx + dy * dy + dz * dz;
            if (sq <= bestSq)
            {
                bestSq = sq;
                bestId = id;
            }
        }

        return bestId;
    }

    // ── Section kind mapping ──────────────────────────────────────────────────

    private static BeamSectionKind? MapSectionKind(string sectionType) =>
        sectionType.ToUpperInvariant() switch
        {
            "ANG"  => BeamSectionKind.L,
            "BEAM" => BeamSectionKind.H,
            "BSC"  => BeamSectionKind.Channel,
            "BULB" => BeamSectionKind.Bar,
            "FBAR" => BeamSectionKind.Bar,
            "RBAR" => BeamSectionKind.Rod,
            "TUBE" => BeamSectionKind.Tube,
            _      => (BeamSectionKind?)null,
        };
}
