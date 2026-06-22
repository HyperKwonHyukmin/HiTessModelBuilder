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
        int pipePidBase = 1000)
    {
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

    // ── Header (Executive + Case Control) ────────────────────────────────────

    private static void WriteHeader(TextWriter w, AnalysisCase? analysis)
    {
        w.WriteLine("SOL 101");
        w.WriteLine("CEND");
        w.WriteLine("DISPLACEMENT = ALL");
        w.WriteLine("FORCE = ALL");
        w.WriteLine("STRESS = ALL");
        w.WriteLine("SPCFORCES = ALL");
        w.WriteLine("SUBCASE 1");
        w.WriteLine("  LABEL = STATIC");
        if (analysis is not null)
        {
            w.WriteLine($"  SPC = {analysis.SpcSid}");
            w.WriteLine($"  LOAD = {analysis.LoadSid}");
            w.WriteLine("  ANALYSIS = STATICS");
        }
        else
        {
            w.WriteLine("  SPC = 1");
        }
        w.WriteLine("BEGIN BULK");
        w.WriteLine("PARAM,POST,-1");
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
                orientation = BeamOrientation.CalculateBarOrientation(nA.Position, nB.Position);
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
        string typeName = SectionTypeName(s.Kind);
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
        foreach (var d in s.Dims)
            sb.Append(BdfField.R(d));
        sb.Append(BdfField.R("0.0")); // NSM
        w.WriteLine(sb.ToString());
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
        BeamSectionKind.H       => "H",
        BeamSectionKind.L       => "L",
        BeamSectionKind.Rod     => "ROD",
        BeamSectionKind.Tube    => "TUBE",
        BeamSectionKind.Box     => "BOX",
        BeamSectionKind.Channel => "CHAN",
        BeamSectionKind.Bar     => "BAR",
        _                       => kind.ToString().ToUpperInvariant(),
    };
}
