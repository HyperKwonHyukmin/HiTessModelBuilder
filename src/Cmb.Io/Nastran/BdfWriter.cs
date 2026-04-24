using Cmb.Core.Model;
using Cmb.Core.Model.Context;

namespace Cmb.Io.Nastran;

public static class BdfWriter
{
    public static void Write(FeModel model, TextWriter w, IEnumerable<int>? spcNodeIds = null)
    {
        WriteHeader(w);
        WriteNodes(model, w);
        WriteElements(model, w);
        WriteProperties(model, w);
        WriteMaterials(model, w);
        WriteRigids(model, w);
        WritePointMasses(model, w);
        WriteSpc(spcNodeIds, w);
        w.WriteLine("ENDDATA");
    }

    // ── Header (Executive + Case Control) ────────────────────────────────────

    private static void WriteHeader(TextWriter w)
    {
        w.WriteLine("SOL 101");
        w.WriteLine("CEND");
        w.WriteLine("DISPLACEMENT = ALL");
        w.WriteLine("STRESS = ALL");
        w.WriteLine("SPCFORCES = ALL");
        w.WriteLine("SUBCASE 1");
        w.WriteLine("  LABEL = STATIC");
        w.WriteLine("  SPC = 1");
        w.WriteLine("BEGIN BULK");
        w.WriteLine("PARAM,POST,-1");
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
        foreach (var e in model.Elements)
        {
            w.WriteLine(
                BdfField.L("CBEAM") +
                BdfField.R(e.Id) +
                BdfField.R(e.PropertyId) +
                BdfField.R(e.StartNodeId) +
                BdfField.R(e.EndNodeId) +
                BdfField.R(e.Orientation.X) +
                BdfField.R(e.Orientation.Y) +
                BdfField.R(e.Orientation.Z) +
                BdfField.R("BGG"));
        }
    }

    // ── PBEAML ───────────────────────────────────────────────────────────────

    private static void WriteProperties(FeModel model, TextWriter w)
    {
        foreach (var s in model.Sections)
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

    private static void WriteRigids(FeModel model, TextWriter w)
    {
        foreach (var r in model.Rigids)
        {
            if (r.DependentNodeIds.Count == 0) continue;

            var sb = new System.Text.StringBuilder();
            sb.Append(BdfField.L("RBE2"));
            sb.Append(BdfField.R(r.Id));
            sb.Append(BdfField.R(r.IndependentNodeId));
            sb.Append(BdfField.R("123456"));
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
            w.WriteLine(
                BdfField.L("CONM2") +
                BdfField.R(pm.Id) +
                BdfField.R(pm.NodeId) +
                BdfField.R(0) +
                BdfField.R(pm.Mass));
        }
    }

    // ── SPC1 ─────────────────────────────────────────────────────────────────

    private static void WriteSpc(IEnumerable<int>? nodeIds, TextWriter w)
    {
        if (nodeIds is null) return;
        foreach (int id in nodeIds)
        {
            w.WriteLine(
                BdfField.L("SPC1") +
                BdfField.R(1) +
                BdfField.R("123456") +
                BdfField.R(id));
        }
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
