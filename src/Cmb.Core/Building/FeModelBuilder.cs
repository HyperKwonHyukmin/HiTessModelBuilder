using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Model.Raw;

namespace Cmb.Core.Building;

public sealed class FeModelBuilder
{
    public FeModel Build(RawDesignData data)
    {
        var model     = new FeModel();
        var nodeAlloc = new IdAllocator(1);
        var elemAlloc = new IdAllocator(1); // CBEAM, CONM2, RBE2 공유 ID 공간
        var secAlloc  = new IdAllocator(1);

        var nodeMap  = new Dictionary<(long, long, long), int>();
        var nodeById = new Dictionary<int, Node>();
        var secMap   = new Dictionary<string, int>();

        model.Materials.Add(Material.DefaultSteel);
        int matId = model.Materials[0].Id;

        BuildStructure(data.Beams,  model, nodeAlloc, elemAlloc, secAlloc, nodeMap, nodeById, secMap, matId);
        BuildPipes    (data.Pipes,  model, nodeAlloc, elemAlloc, secAlloc, nodeMap, nodeById, secMap, matId);
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
        return id;
    }

    private static long Quant(double v) => (long)Math.Round(v * 1000.0);

    // ── Section helpers ───────────────────────────────────────────────────────

    private static int GetOrAddSection(
        FeModel model, IdAllocator alloc,
        Dictionary<string, int> map,
        BeamSectionKind kind, double[] dims, int matId)
    {
        var key = $"{kind}|{string.Join("|", dims.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}";
        if (map.TryGetValue(key, out int id)) return id;
        id = alloc.Next();
        model.Sections.Add(new BeamSection(id, kind, dims, matId));
        map[key] = id;
        return id;
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
        Dictionary<string, int> secMap, int matId)
    {
        // ── 로컬 헬퍼 (외부 컨텍스트 캡처) ───────────────────────────────────

        int Node(double x, double y, double z) =>
            GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, x, y, z);

        int Section(double outDia, double thick) =>
            GetOrAddSection(model, secAlloc, secMap, BeamSectionKind.Tube,
                NormalizeDims(BeamSectionKind.Tube, [outDia, thick]), matId);

        void PipeElem(int nA, int nB, int secId, string name)
        {
            if (nA == nB) return;
            var elem = new BeamElement(elemAlloc.Next(), nA, nB, secId, EntityCategory.Pipe, Vector3.UnitZ, sourceName: name);
            model.Elements.Add(elem);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: elem.Id, note: name);
        }

        void AddMass(double x, double y, double z, double mass, string name)
        {
            int n = Node(x, y, z);
            var pm = new PointMass(elemAlloc.Next(), n, mass, sourceName: name);
            model.PointMasses.Add(pm);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: pm.Id, note: name);
        }

        // ── 타입별 처리 ───────────────────────────────────────────────────────

        foreach (var row in rows)
        {
            var t = row.Type.ToUpperInvariant();

            // UBOLT: PointMass + RBE2 마커 (UboltRbeStage가 구조체에 snap)
            if (t == "UBOLT")
            {
                if (row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                if (row.Pos.Length >= 3)
                {
                    int n = Node(row.Pos[0], row.Pos[1], row.Pos[2]);
                    var rbe = new RigidElement(elemAlloc.Next(), n, [], remark: "UBOLT", sourceName: row.Name);
                    model.Rigids.Add(rbe);
                    model.AddTrace(TraceAction.ElementCreated, "initial", elementId: rbe.Id, note: row.Name);
                }
                continue;
            }

            // ATTA: PointMass only
            if (t == "ATTA")
            {
                if (row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            // OutDia ≤ 0: 단면 정보 없음 → PointMass로 대체
            if (row.OutDia <= 0)
            {
                if (row.Mass > 0 && row.Pos.Length >= 3)
                    AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
                continue;
            }

            // 인라인 장비 (밸브/트랩/필터 등): PointMass only
            if (t is "VALV" or "TRAP" or "FILT" or "EXP" or "VTWA")
            {
                if (row.Mass > 0 && row.Pos.Length >= 3)
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

            // 피팅 부착 질량 (FLAN 제외 모든 배관 타입)
            if (row.Mass > 0 && row.Pos.Length >= 3)
                AddMass(row.Pos[0], row.Pos[1], row.Pos[2], row.Mass, row.Name);
        }
    }

    // ── Equipment ─────────────────────────────────────────────────────────────

    private static void BuildEquips(
        IReadOnlyList<RawEquipRow> rows,
        FeModel model,
        IdAllocator nodeAlloc, IdAllocator elemAlloc,
        Dictionary<(long, long, long), int> nodeMap,
        Dictionary<int, Node> nodeById)
    {
        foreach (var row in rows)
        {
            if (row.Mass <= 0 || row.Cog.Length < 3) continue;
            int n = GetOrAddNode(model, nodeAlloc, nodeMap, nodeById, row.Cog[0], row.Cog[1], row.Cog[2]);
            var equipMass = new PointMass(elemAlloc.Next(), n, row.Mass, sourceName: row.Name);
            model.PointMasses.Add(equipMass);
            model.AddTrace(TraceAction.ElementCreated, "initial", elementId: equipMass.Id, note: row.Name);
        }
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
