using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;

namespace Cmb.Io.Nastran;

public sealed record AnalysisCase(
    int SpcSid = 1,
    int LoadSid = 2,
    double GravG = 9800.0,
    double GravNx = 0.0,
    double GravNy = 0.0,
    double GravNz = -1.0);

public static class BdfWriter
{
    public static void Write(
        FeModel model,
        TextWriter w,
        IEnumerable<int>? spcNodeIds = null,
        AnalysisCase? analysis = null,
        bool forceFullDofUbolt = false,
        int pipePidBase = 101)
    {
        WriteHullComment(model, w);
        WriteHeader(w, analysis);
        WriteNodes(model, w);
        WriteElements(model, w);
        WriteProperties(model, w, pipePidBase);
        WriteMaterials(model, w);
        WriteRigids(model, w, forceFullDofUbolt);
        WritePointMasses(model, w);
        WriteSpc(spcNodeIds, w, analysis?.SpcSid ?? 1);
        if (analysis is not null) WriteGrav(w, analysis);
        w.WriteLine("ENDDATA");
    }

    // ── 호선 주석 ────────────────────────────────────────────────────────────
    // 파일 맨 첫 줄. 해석에는 영향 없고, 권상·LogBook 단계가 BDF 만 보고 호선을 알아내는 통로다.
    // 형식은 PipePidBase 주석과 같은 "$ HiTessModelBuilder: Key=Value" — nastran_bridge 가 같은 정규식 계열로 읽는다.
    private static void WriteHullComment(FeModel model, TextWriter w)
    {
        var hullNo = FeModel.NormalizeHullNo(model.HullNo);
        if (hullNo is not null)
            w.WriteLine($"$ HiTessModelBuilder: HullNo={hullNo}");
    }

    // ── Header (Executive + Case Control) ────────────────────────────────────

    private static void WriteHeader(TextWriter w, AnalysisCase? analysis)
    {
        w.WriteLine("SOL 101");
        w.WriteLine("CEND");
        w.WriteLine("DISPLACEMENT = ALL");
        w.WriteLine("FORCE = ALL");
        w.WriteLine("STRESS = ALL");
        w.WriteLine("SPCFORCES = ALL");
        // RBE2(U-bolt·연결 RBE) 구속력 — 배관 지지 반력을 회수하려면 필요하다.
        w.WriteLine("MPCFORCES = ALL");
        w.WriteLine("SUBCASE 1");
        w.WriteLine("  LABEL = STATIC");
        if (analysis is not null)
        {
            // 해석(--run-nastran) 경로에서만 SPC/LOAD Case Control 을 출력한다.
            // analysis 가 null 인 일반(최종 모델) 출력에는 SPC 경계조건·하중 카드가 없으므로
            // "SPC = N" / "LOAD = N" 라인을 쓰지 않는다(존재하지 않는 set 을 참조하지 않도록).
            w.WriteLine($"  SPC = {analysis.SpcSid}");
            w.WriteLine($"  LOAD = {analysis.LoadSid}");
        }
        // ANALYSIS = STATICS 는 SOL 종류(정적해석) 명시이므로 일반(형상 전용) 출력에도 항상 포함한다.
        // SPC=/LOAD= 와 달리 존재하지 않는 set 을 참조하지 않아 dangling 위험이 없다.
        w.WriteLine("  ANALYSIS = STATICS");
        w.WriteLine("BEGIN BULK");
        w.WriteLine("PARAM,POST,-1");
        // 격자점 무게 요약(GPWG) — 모델 총 질량·무게중심을 F06 에서 입력 중량과 대조하는 용도.
        w.WriteLine("PARAM,GRDPNT,0");
        if (analysis is not null)
            w.WriteLine("PARAM,AUTOMPC,YES");
    }

    // ── GRID ─────────────────────────────────────────────────────────────────

    private static void WriteNodes(FeModel model, TextWriter w)
    {
        foreach (var n in model.Nodes)
        {
            w.WriteLine(
                BdfField.L("GRID") +
                BdfField.R(n.Id) +
                BdfField.R("") +
                BdfField.R(n.Position.X) +
                BdfField.R(n.Position.Y) +
                BdfField.R(n.Position.Z));
        }
    }

    // ── CBEAM ─────────────────────────────────────────────────────────────────

    private static void WriteElements(FeModel model, TextWriter w)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        foreach (var e in model.Elements)
        {
            var orientation = e.Orientation;
            if (nodeById.TryGetValue(e.StartNodeId, out var nA) &&
                nodeById.TryGetValue(e.EndNodeId, out var nB) &&
                nA.Position.DistanceTo(nB.Position) > 1e-9)
            {
                // 요소 방향(CSV ori 유래)을 쓰되, 축과 평행하면 기본 규칙으로 — 평행 v 는 Nastran FATAL.
                orientation = BeamOrientation.Resolve(nA.Position, nB.Position, e.Orientation);
            }

            w.WriteLine(
                BdfField.L("CBEAM") +
                BdfField.R(e.Id) +
                BdfField.R(e.PropertyId) +
                BdfField.R(e.StartNodeId) +
                BdfField.R(e.EndNodeId) +
                BdfField.ROrientation(orientation.X) +
                BdfField.ROrientation(orientation.Y) +
                BdfField.ROrientation(orientation.Z) +
                BdfField.R("BGG"));
        }
    }

    // ── PBEAML ───────────────────────────────────────────────────────────────

    // Structure / Pipe section을 PID 범위로 분리해 출력한다.
    // - Structure: ID < pipePidBase
    // - Pipe:      ID >= pipePidBase
    // 외부 뷰어가 BDF만 받았을 때 marker 주석 + ID 임계값으로 카테고리를 식별할 수 있다.
    // ($ 라인은 Nastran 주석으로 해석에 영향 없음)
    private static void WriteProperties(FeModel model, TextWriter w, int pipePidBase)
    {
        var structSections = model.Sections.Where(s => s.Id <  pipePidBase).ToList();
        var pipeSections   = model.Sections.Where(s => s.Id >= pipePidBase).ToList();

        w.WriteLine($"$ HiTessModelBuilder: PipePidBase={pipePidBase}");

        if (structSections.Count > 0)
        {
            w.WriteLine($"$ ---- STRUCTURE SECTIONS (PID < {pipePidBase}) ----");
            foreach (var s in structSections) WritePbeaml(s, w);
        }

        if (pipeSections.Count > 0)
        {
            w.WriteLine($"$ ---- PIPE SECTIONS (PID >= {pipePidBase}) ----");
            foreach (var s in pipeSections) WritePbeaml(s, w);
        }
    }

    private static void WritePbeaml(BeamSection s, TextWriter w)
    {
        var (typeName, dims) = ResolvePbeamlOutput(s);

        // Header line
        w.WriteLine(
            BdfField.L("PBEAML") +
            BdfField.R(s.Id) +
            BdfField.R(s.MaterialId) +
            BdfField.R("") +
            BdfField.R(typeName));

        // Dims line (blank leading field = continuation)
        var sb = new System.Text.StringBuilder();
        sb.Append(BdfField.L(""));
        foreach (var d in dims)
            sb.Append(BdfField.RPrecise(d));
        sb.Append(BdfField.R("0.0")); // NSM
        w.WriteLine(sb.ToString());
    }

    // PBEAML 헤더의 TYPE 이름과 dims 라인을 결정한다.
    // ── BEAM(H) 단면을 표준 I 단면(MSCBML0 "I")으로 출력 ──
    //   내부 저장형(H): [H−2TF, 2TF, BF, TW]  (FeModelBuilder.NormalizeDims)
    //   I 출력형(6 dims): DIM1=H, DIM2=BF(하부 플랜지 폭), DIM3=BF(상부 플랜지 폭),
    //                     DIM4=TW(웹 두께), DIM5=TF(하부 플랜지 두께), DIM6=TF(상부 플랜지 두께)
    //   복원식: H = d0 + d1,  BF = d2,  TW = d3,  TF = d1 / 2
    //   예) BEAM_200x200x8x12 → 저장형 [176, 24, 200, 8] → I [200, 200, 200, 8, 12, 12]
    private static (string typeName, IReadOnlyList<double> dims) ResolvePbeamlOutput(BeamSection s)
    {
        if (s.Kind == BeamSectionKind.H && s.Dims.Length >= 4)
        {
            double h  = s.Dims[0] + s.Dims[1]; // (H−2TF) + 2TF = H
            double bf = s.Dims[2];             // flange width
            double tw = s.Dims[3];             // web thickness
            double tf = s.Dims[1] / 2.0;       // single-flange thickness
            return ("I", new[] { h, bf, bf, tw, tf, tf });
        }
        return (SectionTypeName(s.Kind), s.Dims);
    }

    // ── MAT1 ─────────────────────────────────────────────────────────────────

    private static void WriteMaterials(FeModel model, TextWriter w)
    {
        foreach (var m in model.Materials)
        {
            w.WriteLine(
                BdfField.L("MAT1") +
                BdfField.R(m.Id) +
                BdfField.R(m.E) +
                BdfField.R("") +
                BdfField.R(m.Nu) +
                BdfField.RSci(m.Rho));
        }
    }

    // ── RBE2 ─────────────────────────────────────────────────────────────────

    private static void WriteRigids(FeModel model, TextWriter w, bool forceFullDofUbolt)
    {
        foreach (var r in model.Rigids)
        {
            if (r.DependentNodeIds.Count == 0) continue;

            // UBOLT(BOX 포함, pipe 일반 UBOLT 모두)는 입력 CSV에서 cm="23" 등 부분 자유도로
            // 들어오면 indep 노드의 T1 등이 풀려 mechanism(Nastran FATAL 9050)을 만들 수 있음.
            // forceFullDofUbolt 옵션이 켜지면 모든 UBOLT의 cm을 "123456"으로 강제 출력.
            string cmOut = (forceFullDofUbolt && r.Remark == "UBOLT")
                ? "123456"
                : r.Cm;

            var sb = new System.Text.StringBuilder();
            sb.Append(BdfField.L("RBE2"));
            sb.Append(BdfField.R(r.Id));
            sb.Append(BdfField.R(r.IndependentNodeId));
            sb.Append(BdfField.R(cmOut));
            int fieldsUsed = 4;

            foreach (int dep in r.DependentNodeIds)
            {
                if (fieldsUsed >= 9)
                {
                    sb.Append(BdfField.L("+"));
                    w.WriteLine(sb.ToString());
                    sb.Clear();
                    sb.Append(BdfField.L("+"));
                    fieldsUsed = 1;
                }
                sb.Append(BdfField.R(dep));
                fieldsUsed++;
            }

            w.WriteLine(sb.ToString());
        }
    }

    // ── CONM2 ────────────────────────────────────────────────────────────────

    private static void WritePointMasses(FeModel model, TextWriter w)
    {
        foreach (var pm in model.PointMasses)
        {
            // CONM2: EID, G, CID, M 뒤에 오프셋 X1/X2/X3 필드까지 명시적으로 출력한다.
            // (질량이 격자점에 위치 → 오프셋은 항상 0.0. 생략하면 일부 후처리/파서가 카드를
            //  불완전한 것으로 취급하므로 0.0 0.0 0.0 을 채워서 표준 양식을 유지한다.)
            w.WriteLine(
                BdfField.L("CONM2") +
                BdfField.R(pm.Id) +
                BdfField.R(pm.NodeId) +
                BdfField.R(0) +
                BdfField.RMass(pm.Mass) +
                BdfField.R(0.0) +
                BdfField.R(0.0) +
                BdfField.R(0.0));
        }
    }

    // ── SPC1 ─────────────────────────────────────────────────────────────────

    private static void WriteSpc(IEnumerable<int>? nodeIds, TextWriter w, int sid)
    {
        if (nodeIds is null) return;
        foreach (int id in nodeIds)
        {
            w.WriteLine(
                BdfField.L("SPC1") +
                BdfField.R(sid) +
                BdfField.R("123456") +
                BdfField.R(id));
        }
    }

    // ── GRAV ─────────────────────────────────────────────────────────────────

    private static void WriteGrav(TextWriter w, AnalysisCase a)
    {
        w.WriteLine(
            BdfField.L("GRAV") +
            BdfField.R(a.LoadSid) +
            BdfField.R("") +
            BdfField.R(a.GravG) +
            BdfField.R(a.GravNx) +
            BdfField.R(a.GravNy) +
            BdfField.R(a.GravNz));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SectionTypeName(BeamSectionKind kind) => kind switch
    {
        BeamSectionKind.H       => "I", // BEAM(H) 단면은 표준 I 단면으로 출력 (ResolvePbeamlOutput가 dims도 I 6-dim으로 변환)
        BeamSectionKind.L       => "L",
        BeamSectionKind.Rod     => "ROD",
        BeamSectionKind.Tube    => "TUBE",
        BeamSectionKind.Box     => "BOX",
        BeamSectionKind.Channel => "CHAN",
        BeamSectionKind.Bar     => "BAR",
        _                       => kind.ToString().ToUpperInvariant(),
    };
}
