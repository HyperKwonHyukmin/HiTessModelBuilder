// M-1 리팩터: Program.cs 에서 분리한 SPC/UBOLT 태깅(모델 mutate + phase JSON 후처리). 동작 불변.
using System.Text.Encodings.Web;
using System.Text.Json;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;

internal static class SpcTagging
{
    // ── --ubolt-full-fix 적용: 모델의 UBOLT RBE2 cm을 "123456"으로 일괄 교체 ──
    // RigidElement.Cm 은 immutable 이므로 동일 ID/Indep/Dep 으로 새 인스턴스를 만들어 리스트를 교체한다.
    // 최종 BDF/JSON 직전에 호출하여 두 산출물이 동일한 cm을 보고하도록 한다 (phase JSON 은 보존).
    internal static int ApplyUboltFullDof(FeModel model)
    {
        int replaced = 0;
        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var r = model.Rigids[i];
            if (r.Remark != "UBOLT" || r.Cm == "123456") continue;
            model.Rigids[i] = new RigidElement(
                r.Id, r.IndependentNodeId, r.DependentNodeIds,
                r.Remark, r.SourceName, "123456", r.DetailRemark);
            replaced++;
        }
        return replaced;
    }
    
    // ── 최종 BDF SPC1에 포함될 노드들에 NodeTags.Spc 부여 ──
    // 모델을 mutate 하므로 final JSON 은 자동으로 Spc 태그를 직렬화한다.
    // 이미 작성된 phase JSON 은 PatchPhaseJsonsWithSpcTag 로 후처리.
    internal static int ApplySpcTag(FeModel model, HashSet<int> spcNodeIdSet)
    {
        if (spcNodeIdSet.Count == 0) return 0;
        int tagged = 0;
        foreach (var n in model.Nodes)
        {
            if (!spcNodeIdSet.Contains(n.Id)) continue;
            if (n.HasTag(NodeTags.Spc)) continue;
            n.AddTag(NodeTags.Spc);
            tagged++;
        }
        return tagged;
    }
    
    // ── 이미 디스크에 작성된 phase JSON / detail JSON 에 "Spc" 태그를 주입 ──
    // 호출 시점: 파이프라인 완료 후 최종 spcNodeIds 가 결정된 직후, BDF/Final JSON 작성 전.
    // 메타·연결성·델타 등 phase 시점 데이터를 보존하기 위해 JsonNode 로 in-place mutation 한다.
    // 00_InputAudit.json / 00_StageSummary.json 은 노드 배열이 없으므로 스킵한다.
    internal static int PatchPhaseJsonsWithSpcTag(string outDir, HashSet<int> spcNodeIdSet)
    {
        if (spcNodeIdSet.Count == 0) return 0;
    
        var paths = new List<string>();
        paths.AddRange(Directory.EnumerateFiles(outDir, "*.json", SearchOption.TopDirectoryOnly));
        string detailDir = Path.Combine(outDir, "detail");
        if (Directory.Exists(detailDir))
            paths.AddRange(Directory.EnumerateFiles(detailDir, "*.json", SearchOption.TopDirectoryOnly));
    
        var writerOpts = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        int filesUpdated = 0;
        foreach (var path in paths)
        {
            var fname = Path.GetFileName(path);
            if (fname.StartsWith("00_", StringComparison.Ordinal)) continue; // InputAudit / StageSummary
    
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { Console.WriteLine($"[경고] {path}: {ex.Message}"); continue; }
    
            System.Text.Json.Nodes.JsonNode? root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(text); }
            catch (Exception ex) { Console.WriteLine($"[경고] {path}: {ex.Message}"); continue; }
            if (root is not System.Text.Json.Nodes.JsonObject obj) continue;
            if (obj["nodes"] is not System.Text.Json.Nodes.JsonArray nodesArr) continue;
    
            bool changed = false;
            foreach (var nNode in nodesArr)
            {
                if (nNode is not System.Text.Json.Nodes.JsonObject nObj) continue;
                int id = nObj["id"]?.GetValue<int>() ?? 0;
                if (id <= 0 || !spcNodeIdSet.Contains(id)) continue;
    
                if (nObj["tags"] is not System.Text.Json.Nodes.JsonArray tagsArr)
                {
                    tagsArr = new System.Text.Json.Nodes.JsonArray();
                    nObj["tags"] = tagsArr;
                }
                bool already = false;
                foreach (var t in tagsArr)
                    if (t is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s == "Spc")
                    { already = true; break; }
                if (already) continue;
                tagsArr.Add("Spc");
                changed = true;
            }
            if (changed)
            {
                // JsonNode.ToJsonString(options) 는 TypeInfoResolver 미설정 시 throw 하므로
                // Utf8JsonWriter 경로를 사용한다 (source-gen 의존성 없이 인덴트만 제어).
                using var fs = File.Create(path);
                using var w  = new Utf8JsonWriter(fs, writerOpts);
                obj.WriteTo(w);
                filesUpdated++;
            }
        }
        return filesUpdated;
    }
    
}
