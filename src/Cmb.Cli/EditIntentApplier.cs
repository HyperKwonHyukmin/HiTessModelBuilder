// M-1 리팩터: Program.cs 에서 분리한 편집 인텐트 적용/검증 로직(apply-edit-intent). 동작 불변.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmb.Core.Building;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Model.Raw;
using Cmb.Core.Serialization;
using Cmb.Io.Csv;
using Cmb.Io.Nastran;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using Microsoft.Extensions.Logging;

internal static class EditIntentApplier
{
    internal static int ApplyEditIntent(DirectoryInfo folder, DirectoryInfo? outDirOption, bool strict)
    {
        if (!folder.Exists)
        {
            Console.Error.WriteLine($"[오류] 폴더를 찾을 수 없습니다: {folder.FullName}");
            return 64;
        }
    
        var editFiles = folder.GetFiles("*_edit.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        if (editFiles.Count == 0)
        {
            Console.WriteLine("no edit intent file");
            return 2;
        }
        if (editFiles.Count > 1)
            Console.Error.WriteLine($"[경고] *_edit.json {editFiles.Count}개 중 최신 파일을 사용합니다: {editFiles[0].Name}");
    
        var editFile = editFiles[0];
        EditIntentDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<EditIntentDocument>(
                File.ReadAllText(editFile.FullName),
                EditIntentJson.Options) ?? throw new JsonException("edit intent document is null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] edit intent JSON을 읽을 수 없습니다: {ex.Message}");
            return 64;
        }
    
        if (doc.SchemaVersion != "1.0")
        {
            Console.Error.WriteLine($"[오류] unsupported schemaVersion: {doc.SchemaVersion}");
            return 64;
        }
        if (doc.Intents.Count == 0)
        {
            Console.Error.WriteLine("[경고] edit intent가 비어 있습니다.");
            return 2;
        }
    
        var baseName = editFile.Name[..^"_edit.json".Length];
        var stageJson = new FileInfo(Path.Combine(folder.FullName, baseName + ".json"));
        var baseBdf = new FileInfo(Path.Combine(folder.FullName, baseName + ".bdf"));
        if (!stageJson.Exists)
        {
            Console.Error.WriteLine($"[오류] base JSON을 찾을 수 없습니다: {stageJson.FullName}");
            return 64;
        }
        if (!baseBdf.Exists)
            Console.Error.WriteLine($"[경고] base BDF를 찾을 수 없어 JSON에서 BDF를 재생성합니다: {baseBdf.FullName}");
    
        string stageJsonText;
        try { stageJsonText = File.ReadAllText(stageJson.FullName); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] base JSON을 읽을 수 없습니다: {ex.Message}");
            return 64;
        }
    
        StageMetaSnapshot meta;
        try { meta = ReadStageMeta(stageJsonText); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] base JSON meta를 읽을 수 없습니다: {ex.Message}");
            return 64;
        }
    
        if (!string.Equals(doc.StageRef.Phase, meta.Phase, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[오류] stageRef.phase mismatch: edit={doc.StageRef.Phase}, stage={meta.Phase}");
            return 64;
        }
        if (!string.Equals(doc.StageRef.StageName, meta.StageName, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[오류] stageRef.stageName mismatch: edit={doc.StageRef.StageName}, stage={meta.StageName}");
            return 64;
        }
    
        FeModel model;
        try
        {
            model = FeModelJson.FromJson(stageJsonText);
            model.SyncIdCounters();
            // 호선은 base JSON meta.hullNo 가 원본이다. 이 기능 이전 JSON 이면 base BDF 의 주석에서 찾는다.
            if (model.HullNo is null && baseBdf.Exists)
                model.HullNo = ReadHullNoFromBdf(baseBdf.FullName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] base JSON을 FeModel로 변환할 수 없습니다: {ex.Message}");
            return 64;
        }
    
        var baseConnectivity = ConnectivityAnalyzer.Analyze(model);
        var baseGroupsById = baseConnectivity.Groups.ToDictionary(g => g.Id);
        var operations = new List<Dictionary<string, object?>>();
        // addNode 가 만들 GRID — 검증 시점엔 아직 모델에 없지만 addRigid 가 참조할 수 있다.
        var addedNodeIds = CollectAddedNodeIds(doc.Intents);
        var addedElementIds = new HashSet<int>();

        foreach (var intent in doc.Intents)
        {
            string status = intent.Validation.Status;
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[오류] intent {intent.Id} has viewer validation error.");
                return 65;
            }
            if (string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string warning in intent.Validation.Warnings)
                    operations.Add(EditTrace.Operation("INTENT_VALIDATION_WARNING", "warning", intent.Id, details: warning));
                if (strict)
                {
                    Console.Error.WriteLine($"[오류] intent {intent.Id} has viewer validation warning (--strict).");
                    return 65;
                }
            }
    
            string? validationError = ValidateIntent(intent, model, baseGroupsById, addedNodeIds);
            if (validationError is null && intent.Kind == "addElement" &&
                !addedElementIds.Add(intent.Params.GetProperty("elementId").GetInt32()))
                validationError = $"addElement: elementId {intent.Params.GetProperty("elementId").GetInt32()} 가 두 번 나옵니다.";
            if (validationError is not null)
            {
                Console.Error.WriteLine($"[오류] intent {intent.Id} rejected: {validationError}");
                return 65;
            }
        }
    
        var outputDir = outDirOption?.FullName ?? Path.Combine(folder.FullName, "edited");
        var tempDir = Path.Combine(folder.FullName, $".edit_tmp_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var session = new EditSession();
            foreach (var intent in OrderIntents(doc.Intents, model))
            {
                if (intent.Kind == "deleteGroup")
                {
                    int groupId = intent.Params.GetProperty("groupId").GetInt32();
                    session.ExplicitlyDeletedNodes.UnionWith(baseGroupsById[groupId].NodeIds);
                    ApplyDeleteGroup(model, baseGroupsById[groupId], intent, operations);
                }
                else if (intent.Kind == "deleteRigid")
                {
                    ApplyDeleteRigid(model, intent, session, operations);
                }
                else if (intent.Kind == "addNode")
                {
                    ApplyAddNode(model, intent, session, operations);
                }
                else if (intent.Kind == "deleteElements")
                {
                    ApplyDeleteElements(model, intent, session, operations);
                }
                else if (intent.Kind == "deleteMasses")
                {
                    ApplyDeleteMasses(model, intent, session, operations);
                }
                else if (intent.Kind == "moveNodes")
                {
                    ApplyMoveNodes(model, intent, operations);
                }
                else if (intent.Kind == "addElement")
                {
                    ApplyAddElement(model, intent, session, operations);
                }
                else if (intent.Kind == "deleteOrphanNodes")
                {
                    // 지우는 시점은 맨 끝(RemoveOrphanCandidates) — 뒤에 오는 편집이 그 노드를 쓸 수 있다.
                    session.OrphanCandidates.UnionWith(ReadIntList(intent.Params, "nodeIds"));
                    operations.Add(EditTrace.Operation("INTENT_APPLIED", "info", intent.Id, kind: "deleteOrphanNodes",
                        details: "candidates registered; removed after all intents if unreferenced"));
                }
                else if (intent.Kind == "addRigid")
                {
                    session.CollectAddRigidNodes(intent);
                    ApplyAddRigid(model, intent, operations);
                }
                else if (intent.Kind == "emptyPipeFluid")
                {
                    ApplyEmptyPipeFluid(model, intent, operations);
                }
                else if (intent.Kind == "rotateModel")
                {
                    ApplyRotateModel(model, intent, operations);
                }
                else
                {
                    throw new InvalidOperationException($"unsupported intent kind: {intent.Kind}");
                }
            }
    
            PurgeDanglingReferences(model, operations, intentId: null);
            RemoveOrphanCandidates(model, session, operations);
            ReportUnboundAddedNodes(model, session, operations);

            // RBE 에 쓰겠다고 고른 노드가 결과에서 사라졌거나 어떤 RBE 에도 안 남았으면 잘못된 BDF 를 내지 않는다
            // (bridge F-5 불변식과 같은 관문).
            // 추가한 부재가 끝 노드 유실로 정리(PurgeDanglingReferences)됐으면 실패 — Studio 검증이 막지만 마지막 관문.
            var lostElements = session.AddedElementIds.Where(id => model.Elements.All(e => e.Id != id)).OrderBy(id => id).ToList();
            if (lostElements.Count > 0)
                throw new InvalidOperationException(
                    "편집 적용 결과에서 추가한 부재가 유실되었습니다: " + string.Join(", ", lostElements.Select(id => $"E{id}")));

            var dropped = FindDroppedIntentNodes(model, session);
            if (dropped.Count > 0)
                throw new InvalidOperationException(
                    "편집 적용 결과에서 RBE 에 사용한 노드가 유실되었습니다: " +
                    string.Join(", ", dropped.Select(n => $"N{n}")));

            var diagnostics = ModelDiagnosticsAnalyzer.Analyze(model, new Tolerances());
            var connectivity = ConnectivityAnalyzer.Analyze(model);
            var health = HealthMetricsAnalyzer.Analyze(model, null, new Tolerances());
            health.DiagnosticCounts = ValidationReporting.CountDiagnostics(diagnostics);
            var validation = ValidationReporting.BuildValidationResult(
                diagnostics.Select(ValidationReporting.DiagnosticFromDto).ToList(),
                connectivity.GroupCount,
                model);
    
            var tempJson = Path.Combine(tempDir, baseName + ".json");
            var tempBdf = Path.Combine(tempDir, baseName + ".bdf");
            var tempTrace = Path.Combine(tempDir, "apply-trace.json");
    
            File.WriteAllText(tempJson, model.ToJson(
                phase: meta.Phase,
                stageName: meta.StageName,
                connectivity: connectivity,
                healthMetrics: health,
                stageDiagnostics: diagnostics,
                stageIndex: meta.StageIndex,
                processingDurationMs: 0,
                validationResult: validation));
            var preservedAnalysis = baseBdf.Exists
                ? ReadAnalysisCaseFromBdf(baseBdf.FullName)
                : null;
            var preservedSpcNodeIds = baseBdf.Exists
                ? ReadSpcNodeIdsFromBdf(baseBdf.FullName, model)
                : Array.Empty<int>();
    
            using (var writer = new StreamWriter(tempBdf))
                BdfWriter.Write(model, writer, preservedSpcNodeIds, preservedAnalysis);
    
            var traceDoc = new EditApplyTraceDocument
            {
                AppliedAt = DateTimeOffset.Now.ToString("o"),
                IntentFile = editFile.Name,
                BaseStage = baseName,
                Intents = doc.Intents,
                Operations = operations,
            };
            File.WriteAllText(tempTrace, JsonSerializer.Serialize(traceDoc, EditIntentJson.Options));
    
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
            Directory.Move(tempDir, outputDir);
    
            Console.WriteLine($"{doc.Intents.Count} intents applied, output at {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] apply failed, no output written: {ex.Message}");
            return 70;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
    
    internal static StageMetaSnapshot ReadStageMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("meta");
        return new StageMetaSnapshot(
            meta.GetProperty("phase").GetString() ?? "",
            meta.GetProperty("stageName").GetString() ?? "",
            meta.TryGetProperty("stageIndex", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : 0,
            meta.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "");
    }
    
    internal static AnalysisCase? ReadAnalysisCaseFromBdf(string bdfPath)
    {
        int? spcSid = null;
        int? loadSid = null;
        double? gravG = null;
        double gravNx = 0.0;
        double gravNy = 0.0;
        double gravNz = -1.0;
    
        foreach (var rawLine in File.ReadLines(bdfPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("$", StringComparison.Ordinal)) continue;
    
            if (line.StartsWith("SPC", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                spcSid = ParseCaseControlInt(line);
            else if (line.StartsWith("LOAD", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                loadSid = ParseCaseControlInt(line);
            else if (line.StartsWith("GRAV", StringComparison.OrdinalIgnoreCase))
            {
                var fields = SplitBdfFields(line);
                if (fields.Count >= 6 &&
                    int.TryParse(fields[1], out var gravSid) &&
                    TryParseDouble(fields[^4], out var g) &&
                    TryParseDouble(fields[^3], out var nx) &&
                    TryParseDouble(fields[^2], out var ny) &&
                    TryParseDouble(fields[^1], out var nz))
                {
                    loadSid ??= gravSid;
                    gravG = g;
                    gravNx = nx;
                    gravNy = ny;
                    gravNz = nz;
                }
            }
        }
    
        if (spcSid is null && loadSid is null && gravG is null)
            return null;
    
        return new AnalysisCase(
            SpcSid: spcSid ?? 1,
            LoadSid: loadSid ?? 2,
            GravG: gravG ?? 9800.0,
            GravNx: gravNx,
            GravNy: gravNy,
            GravNz: gravNz);
    }
    
    /// <summary>
    /// 기준 BDF 의 SPC1 노드 중 편집 후 모델에 남아 있는 노드만 이어받는다.
    /// RBE2 종속 노드는 뺀다 — build-full 의 SPC 시딩과 같은 규칙. 편집으로 새로 생긴 RBE2 의 종속이
    /// SPC 노드이면 UM·US 중복으로 Nastran FATAL 2101 이 난다(2026-09-23 GMU 샘플 사다리 하단 4개 노드).
    /// </summary>
    /// <summary>BDF 의 '$ HiTessModelBuilder: HullNo=...' 주석에서 호선을 읽는다. 없으면 null.</summary>
    internal static string? ReadHullNoFromBdf(string bdfPath)
    {
        const string marker = "HullNo=";
        foreach (var rawLine in File.ReadLines(bdfPath))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('$')) continue;
            if (line.IndexOf("HiTessModelBuilder:", StringComparison.OrdinalIgnoreCase) < 0) continue;
            int at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var value = line[(at + marker.Length)..].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            return value.Length > 0 ? FeModel.NormalizeHullNo(value[0]) : null;
        }
        return null;
    }

    internal static IReadOnlyList<int> ReadSpcNodeIdsFromBdf(string bdfPath, FeModel model)
    {
        var rbeDependentIds = model.Rigids.SelectMany(r => r.DependentNodeIds).ToHashSet();
        var existingNodeIds = model.Nodes.Select(n => n.Id)
            .Where(id => !rbeDependentIds.Contains(id))
            .ToHashSet();
        var spcNodeIds = new List<int>();
    
        foreach (var rawLine in File.ReadLines(bdfPath))
        {
            var line = rawLine.TrimEnd();
            if (!line.StartsWith("SPC1", StringComparison.OrdinalIgnoreCase)) continue;
    
            var fields = SplitBdfFields(line);
            if (fields.Count < 4) continue;
    
            foreach (var field in fields.Skip(3))
            {
                if (!int.TryParse(field, out var nodeId)) continue;
                if (existingNodeIds.Contains(nodeId))
                    spcNodeIds.Add(nodeId);
            }
        }
    
        return spcNodeIds.Distinct().OrderBy(id => id).ToList();
    }
    
    internal static int? ParseCaseControlInt(string line)
    {
        var idx = line.IndexOf('=');
        if (idx < 0) return null;
        var value = line[(idx + 1)..].Trim();
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
    
    internal static List<string> SplitBdfFields(string line)
    {
        return line.Contains(',')
            ? line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
    
    internal static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }
    
    internal static string? ValidateIntent(
        EditIntent intent,
        FeModel model,
        IReadOnlyDictionary<int, ConnectivityGroupDto> baseGroupsById,
        IReadOnlySet<int>? addedNodeIds = null)
    {
        addedNodeIds ??= new HashSet<int>();

        if (intent.Kind == "deleteRigid")
        {
            if (!intent.Params.TryGetProperty("rigidId", out var ridProp) ||
                ridProp.ValueKind != JsonValueKind.Number || !ridProp.TryGetInt32(out int rid))
                return "deleteRigid.params.rigidId is required.";
            // Studio 는 원본 RBE 만 deleteRigid 로 지운다(이 세션에서 만든 RBE 는 intent 를 지워 없앤다).
            if (model.Rigids.All(r => r.Id != rid))
                return $"deleteRigid rigidId {rid} not found.";
            return null;
        }

        if (intent.Kind == "addNode")
        {
            if (!intent.Params.TryGetProperty("nodeId", out var nidProp) ||
                nidProp.ValueKind != JsonValueKind.Number || !nidProp.TryGetInt32(out int nid) || nid <= 0)
                return "addNode.params.nodeId must be a positive integer.";
            foreach (string axis in new[] { "x", "y", "z" })
                if (!TryReadFiniteNumber(intent.Params, axis, out _))
                    return $"addNode.params.{axis} must be a finite number.";
            // 조용히 건너뛰면 의도한 좌표가 아닌 기존 노드가 RBE 에 쓰인다 — 거부가 안전하다.
            if (model.FindNode(nid) is not null)
                return $"addNode: Node {nid} already exists.";
            return null;
        }

        if (intent.Kind == "deleteElements")
        {
            if (!TryReadIntList(intent.Params, "elementIds", out var ids) || ids.Count == 0)
                return "deleteElements.params.elementIds must be a non-empty integer array.";
            var known = model.Elements.Select(e => e.Id).ToHashSet();
            var missing = ids.Where(id => !known.Contains(id)).ToList();
            return missing.Count > 0 ? $"deleteElements: element(s) not found: {string.Join(",", missing.Take(8))}." : null;
        }

        if (intent.Kind == "deleteMasses")
        {
            if (!TryReadIntList(intent.Params, "massIds", out var ids) || ids.Count == 0)
                return "deleteMasses.params.massIds must be a non-empty integer array.";
            var known = model.PointMasses.Select(pm => pm.Id).ToHashSet();
            var missing = ids.Where(id => !known.Contains(id)).ToList();
            return missing.Count > 0 ? $"deleteMasses: mass(es) not found: {string.Join(",", missing.Take(8))}." : null;
        }

        if (intent.Kind == "moveNodes")
        {
            if (!intent.Params.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array ||
                moves.GetArrayLength() == 0)
                return "moveNodes.params.moves must be a non-empty array.";
            var seen = new HashSet<int>();
            foreach (var m in moves.EnumerateArray())
            {
                if (!m.TryGetProperty("nodeId", out var nidProp) || nidProp.ValueKind != JsonValueKind.Number ||
                    !nidProp.TryGetInt32(out int nid))
                    return "moveNodes.moves[].nodeId is required.";
                foreach (string axis in new[] { "x", "y", "z" })
                    if (!TryReadFiniteNumber(m, axis, out _))
                        return $"moveNodes: Node {nid} {axis} must be a finite number.";
                if (!seen.Add(nid)) return $"moveNodes: Node {nid} 가 두 번 나옵니다.";
                // 원본 노드만 — 새로 만든 노드는 Studio 가 노드 추가를 다시 기록한다.
                if (model.FindNode(nid) is null) return $"moveNodes: Node {nid} not found.";
            }
            return null;
        }

        if (intent.Kind == "addElement")
        {
            var p = intent.Params;
            int ReadInt(string name) =>
                p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
            int eid = ReadInt("elementId"), a = ReadInt("startNode"), b = ReadInt("endNode"), pid = ReadInt("propertyId");
            if (eid <= 0 || a <= 0 || b <= 0 || pid <= 0)
                return "addElement: elementId/startNode/endNode/propertyId must be positive integers.";
            if (a == b) return "addElement: startNode == endNode.";
            // CBEAM·RBE2·CONM2 는 Nastran 요소 번호 공간을 같이 쓴다.
            if (model.Elements.Any(e => e.Id == eid) || model.Rigids.Any(r => r.Id == eid) || model.PointMasses.Any(pm => pm.Id == eid))
                return $"addElement: elementId {eid} already used.";
            foreach (int n in new[] { a, b })
                if (model.FindNode(n) is null && !addedNodeIds.Contains(n)) return $"addElement: Node {n} not found.";
            // 같은 노드쌍 중복은 Studio 가 막는다 — 원본 부재를 지우고 같은 자리에 새로 만드는 경우가 있어 여기선 보지 않는다.
            if (p.TryGetProperty("newProperty", out var np) && np.ValueKind == JsonValueKind.Object)
            {
                if (!np.TryGetProperty("id", out var npId) || !npId.TryGetInt32(out int npid) || npid != pid)
                    return "addElement.newProperty.id must equal propertyId.";
                if (model.FindSection(pid) is not null) return $"addElement: property {pid} already exists.";
                if (!np.TryGetProperty("kind", out var kp) || kp.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<BeamSectionKind>(kp.GetString(), out _))
                    return "addElement.newProperty.kind is invalid.";
                if (!np.TryGetProperty("dims", out var dp) || dp.ValueKind != JsonValueKind.Array || dp.GetArrayLength() == 0 ||
                    dp.EnumerateArray().Any(d => d.ValueKind != JsonValueKind.Number || !(d.GetDouble() > 0)))
                    return "addElement.newProperty.dims must be positive numbers.";
                if (!np.TryGetProperty("materialId", out var mp) || !mp.TryGetInt32(out int mid) || model.FindMaterial(mid) is null)
                    return "addElement.newProperty.materialId not found.";
            }
            else if (model.FindSection(pid) is null)
            {
                return $"addElement: property {pid} not found.";
            }
            return null;
        }

        if (intent.Kind == "deleteOrphanNodes")
        {
            if (!TryReadIntList(intent.Params, "nodeIds", out var ids) || ids.Count == 0)
                return "deleteOrphanNodes.params.nodeIds must be a non-empty integer array.";
            var missing = ids.Where(id => model.FindNode(id) is null).ToList();
            return missing.Count > 0 ? $"deleteOrphanNodes: node(s) not found: {string.Join(",", missing.Take(8))}." : null;
        }

        if (intent.Kind == "deleteGroup")
        {
            if (!intent.Params.TryGetProperty("groupId", out var groupIdProp) ||
                groupIdProp.ValueKind != JsonValueKind.Number)
                return "deleteGroup.params.groupId is required.";
    
            int groupId = groupIdProp.GetInt32();
            if (!baseGroupsById.TryGetValue(groupId, out var grp))
                return $"groupId {groupId} not in connectivity.";
    
            // viewer가 본 그룹과 CLI 재분석 그룹이 같은 물리 그룹인지 멤버 노드 수로 교차검증.
            // groupId(ordinal)가 양측에서 어긋나면 엉뚱한 그룹이 통째로 삭제되는 무음 데이터 손상이
            // 발생하므로, viewer가 함께 보낸 memberNodeCount 와 불일치하면 안전을 위해 거부한다.
            if (intent.Params.TryGetProperty("memberNodeCount", out var mncProp) &&
                mncProp.ValueKind == JsonValueKind.Number)
            {
                int expected = mncProp.GetInt32();
                if (expected != grp.NodeCount)
                    return $"deleteGroup groupId {groupId} 멤버 불일치: viewer={expected} vs base={grp.NodeCount} " +
                           "(그룹 식별 어긋남 — 안전을 위해 거부).";
            }
            return null;
        }
    
        if (intent.Kind == "addRigid")
        {
            if (!intent.Params.TryGetProperty("dependentNodes", out var depsProp) ||
                depsProp.ValueKind != JsonValueKind.Array)
                return "addRigid.params.dependentNodes is required.";
    
            var nodeIds = model.Nodes.Select(n => n.Id).ToHashSet();
            nodeIds.UnionWith(addedNodeIds);

            // centroid("spider") 모드: createIndependentAtCentroid == true 면 independentNode 불요.
            // 선택 노드들의 평균지점에 새 독립 GRID 를 만들고 그 노드를 GN 으로 RBE2 를 생성한다.
            bool centroidMode =
                intent.Params.TryGetProperty("createIndependentAtCentroid", out var centProp) &&
                centProp.ValueKind == JsonValueKind.True;
            if (centroidMode)
            {
                var centDeps = depsProp.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetInt32())
                    .Distinct()
                    .ToList();
                if (centDeps.Count < 3)
                    return "addRigid(centroid).params.dependentNodes must list at least 3 existing nodes.";
                foreach (int dep in centDeps)
                    if (!nodeIds.Contains(dep)) return $"dependentNode {dep} not found.";
    
                if (intent.Params.TryGetProperty("cm", out var cmCentProp) &&
                    cmCentProp.ValueKind == JsonValueKind.String)
                {
                    string cmCent = cmCentProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(cmCent) && !EditModelHelpers.IsValidCm(cmCent))
                        return $"invalid cm '{cmCent}'.";
                }
    
                // independentCoord 가 주어지면 x/y/z 모두 유한수여야 한다(평균지점 대체).
                if (intent.Params.TryGetProperty("independentCoord", out var coordProp) &&
                    coordProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (string axis in new[] { "x", "y", "z" })
                        if (!TryReadFiniteNumber(coordProp, axis, out _))
                            return $"addRigid(centroid).params.independentCoord.{axis} must be a finite number.";
                }
    
                // mass 가 주어지면 음수가 아닌 유한수여야 한다(CONM2).
                if (intent.Params.TryGetProperty("mass", out var massProp) &&
                    massProp.ValueKind == JsonValueKind.Number)
                {
                    if (!massProp.TryGetDouble(out double mass) || !double.IsFinite(mass) || mass < 0)
                        return "addRigid(centroid).params.mass must be a non-negative finite number.";
                }
                return null;
            }
    
            // 기존 경로: 사전 지정된 independentNode + dependents (그대로 유지).
            if (!intent.Params.TryGetProperty("independentNode", out var indepProp) ||
                indepProp.ValueKind != JsonValueKind.Number)
                return "addRigid.params.independentNode is required.";
    
            int indep = indepProp.GetInt32();
            var deps = depsProp.EnumerateArray().Select(x => x.GetInt32()).Distinct().ToList();
            if (!nodeIds.Contains(indep)) return $"independentNode {indep} not found.";
            if (deps.Count == 0) return "dependentNodes is empty.";
            foreach (int dep in deps)
                if (!nodeIds.Contains(dep)) return $"dependentNode {dep} not found.";
    
            string cm = "";
            if (intent.Params.TryGetProperty("cm", out var cmProp) && cmProp.ValueKind == JsonValueKind.String)
                cm = cmProp.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(cm) && !EditModelHelpers.IsValidCm(cm))
                return $"invalid cm '{cm}'.";
            return null;
        }
    
        if (intent.Kind == "emptyPipeFluid")
        {
            if (!intent.Params.TryGetProperty("materialIds", out var idsProp) ||
                idsProp.ValueKind != JsonValueKind.Array)
                return "emptyPipeFluid.params.materialIds is required.";
            if (!intent.Params.TryGetProperty("targetRho", out var rhoProp) ||
                rhoProp.ValueKind != JsonValueKind.Number ||
                !rhoProp.TryGetDouble(out double targetRho) ||
                !double.IsFinite(targetRho) ||
                targetRho <= 0)
                return "emptyPipeFluid.params.targetRho must be a positive finite number.";
    
            var materialIds = new List<int>();
            foreach (var item in idsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int materialId))
                    return "emptyPipeFluid.params.materialIds must contain integers.";
                materialIds.Add(materialId);
            }
            materialIds = materialIds.Distinct().ToList();
            if (materialIds.Count == 0)
                return "emptyPipeFluid.params.materialIds is empty.";
    
            var knownMaterialIds = model.Materials.Select(material => material.Id).ToHashSet();
            var missingMaterialIds = materialIds.Where(id => !knownMaterialIds.Contains(id)).ToList();
            if (missingMaterialIds.Count > 0)
                return $"emptyPipeFluid materialIds not found: {string.Join(",", missingMaterialIds)}.";
    
            var pipeMaterialIds = model.Elements
                .Where(element => element.Category == EntityCategory.Pipe)
                .Select(element => model.FindSection(element.PropertyId)?.MaterialId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet();
            var nonPipeMaterialIds = materialIds.Where(id => !pipeMaterialIds.Contains(id)).ToList();
            if (nonPipeMaterialIds.Count > 0)
                return $"emptyPipeFluid materialIds are not referenced by Pipe elements: {string.Join(",", nonPipeMaterialIds)}.";
    
            return null;
        }
    
        if (intent.Kind == "rotateModel")
        {
            if (!intent.Params.TryGetProperty("axis", out var axisProp) ||
                axisProp.ValueKind != JsonValueKind.String)
                return "rotateModel.params.axis is required.";
            string? axis = axisProp.GetString();
            if (axis is not ("X" or "Y" or "Z"))
                return "rotateModel.params.axis must be one of X, Y, Z.";
            if (!intent.Params.TryGetProperty("angleDeg", out var angleProp) ||
                angleProp.ValueKind != JsonValueKind.Number ||
                !double.IsFinite(angleProp.GetDouble()))
                return "rotateModel.params.angleDeg must be a finite number.";
            return null;
        }
    
        return $"unsupported intent kind: {intent.Kind}";
    }
    
    /// <summary>편집 한 번의 적용 동안 모으는 상태 — orphan 정리·불변식 검사 재료.</summary>
    internal sealed class EditSession
    {
        /// <summary>
        /// 고립 정리 후보 — deleteRigid 가 붙들던 노드, deleteElements(removeOrphanNodes) 끝 노드,
        /// deleteMasses 의 노드, deleteOrphanNodes 가 고른 노드. 모든 편집을 적용한 뒤 아무것도 안 걸린 것만 지운다
        /// (Studio computeDeleteMask 와 같은 규칙 — 기록 순서와 무관).
        /// </summary>
        public HashSet<int> OrphanCandidates { get; } = new();
        /// <summary>addRigid 가 지목한 노드 — 결과에서 사라지면 안 된다.</summary>
        public HashSet<int> IntentNodes { get; } = new();
        /// <summary>deleteGroup 으로 사용자가 명시적으로 지운 노드 — 불변식 예외.</summary>
        public HashSet<int> ExplicitlyDeletedNodes { get; } = new();
        public List<int> AddedNodeIds { get; } = new();
        /// <summary>addElement 로 만든 부재 — 결과에서 사라지면 실패시킨다.</summary>
        public List<int> AddedElementIds { get; } = new();

        public void CollectAddRigidNodes(EditIntent intent)
        {
            var p = intent.Params;
            if (p.TryGetProperty("dependentNodes", out var deps) && deps.ValueKind == JsonValueKind.Array)
                foreach (var d in deps.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int id)) IntentNodes.Add(id);
            if (p.TryGetProperty("independentNode", out var ind) && ind.ValueKind == JsonValueKind.Number &&
                ind.TryGetInt32(out int indep))
                IntentNodes.Add(indep);
        }
    }

    internal static HashSet<int> CollectAddedNodeIds(IEnumerable<EditIntent> intents)
    {
        var ids = new HashSet<int>();
        foreach (var intent in intents)
            if (intent.Kind == "addNode" &&
                intent.Params.TryGetProperty("nodeId", out var nid) &&
                nid.ValueKind == JsonValueKind.Number && nid.TryGetInt32(out int id))
                ids.Add(id);
        return ids;
    }

    /// <summary>
    /// 적용 순서: rotateModel → 원본 RBE 를 지우는 deleteRigid → addNode → 나머지(기록 순서).
    /// - 회전이 맨 앞: Studio 는 addNode·무게중심 좌표를 '회전된 좌표계' 로 기록한다(pivot 고정이라 회전끼리는 순서 무관).
    /// - deleteRigid 를 당긴다: addRigid 가 '이미 다른 RBE 가 점유한' 종속을 빼므로, 흡수될 RBE 가 뒤에서 지워지면
    ///   방금 뺀 노드가 어디에도 안 남는다. Studio 의 connectRigid 는 한 액션 안에서만 순서를 지킨다.
    ///   원본 모델에 실재하는 rigidId 만 당긴다(nastran_bridge hoist_base_rigid_deletions 와 같은 규칙).
    /// - addNode 는 그 GRID 를 참조하는 addRigid 보다 앞이어야 한다.
    /// - addElement 는 addNode 바로 뒤 — Studio 가 정한 요소 번호(최대+1)를, 뒤에서 RBE·CONM2 를 만들며
    ///   NextUnusedElementId 로 번호를 매기는 addRigid 가 먼저 가져가지 않게 한다.
    /// </summary>
    internal static List<EditIntent> OrderIntents(IReadOnlyList<EditIntent> intents, FeModel model)
    {
        var baseRigidIds = model.Rigids.Select(r => r.Id).ToHashSet();
        bool IsBaseRigidDeletion(EditIntent i) =>
            i.Kind == "deleteRigid" &&
            i.Params.TryGetProperty("rigidId", out var rid) &&
            rid.ValueKind == JsonValueKind.Number && rid.TryGetInt32(out int id) &&
            baseRigidIds.Contains(id);

        var rotations = intents.Where(i => i.Kind == "rotateModel").ToList();
        var deletions = intents.Where(IsBaseRigidDeletion).ToList();
        var additions = intents.Where(i => i.Kind == "addNode").ToList();
        var elements = intents.Where(i => i.Kind == "addElement").ToList();
        var rest = intents.Where(i => i.Kind is not ("rotateModel" or "addNode" or "addElement") && !IsBaseRigidDeletion(i)).ToList();
        return rotations.Concat(deletions).Concat(additions).Concat(elements).Concat(rest).ToList();
    }

    internal static void ApplyDeleteRigid(
        FeModel model,
        EditIntent intent,
        EditSession session,
        List<Dictionary<string, object?>> operations)
    {
        int rigidId = intent.Params.GetProperty("rigidId").GetInt32();
        var removed = model.Rigids.Where(r => r.Id == rigidId).ToList();
        if (removed.Count == 0)
        {
            operations.Add(EditTrace.Operation(
                "INTENT_SKIPPED", "warning", intent.Id,
                kind: "deleteRigid",
                details: $"RBE {rigidId} 가 이미 없음"));
            return;
        }

        foreach (var rigid in removed)
        {
            model.Rigids.Remove(rigid);
            session.OrphanCandidates.Add(rigid.IndependentNodeId);
            session.OrphanCandidates.UnionWith(rigid.DependentNodeIds);
        }

        // removeMass: 장비 COG 오류(EQUIPMENT_COG_FAR) 삭제 — 독립 노드(COG)의 장비 질량도 뺀다.
        // 남기면 연결 없는 GRID 에 CONM2 가 남아 특이 행렬이 된다. 그 노드는 orphan 정리가 지운다.
        int massRemoved = 0;
        bool removeMass = intent.Params.TryGetProperty("removeMass", out var rm) && rm.ValueKind == JsonValueKind.True;
        if (removeMass)
        {
            var hubs = removed.Select(r => r.IndependentNodeId).ToHashSet();
            massRemoved = model.PointMasses.RemoveAll(pm => hubs.Contains(pm.NodeId));
        }

        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "deleteRigid",
            rbeId: rigidId,
            details: $"RBE {rigidId} removed" + (removeMass ? $", {massRemoved} mass(es) on independent node removed" : "")));
    }

    internal static void ApplyAddNode(
        FeModel model,
        EditIntent intent,
        EditSession session,
        List<Dictionary<string, object?>> operations)
    {
        var p = intent.Params;
        int nodeId = p.GetProperty("nodeId").GetInt32();
        TryReadFiniteNumber(p, "x", out double x);
        TryReadFiniteNumber(p, "y", out double y);
        TryReadFiniteNumber(p, "z", out double z);
        if (model.FindNode(nodeId) is not null)
            throw new InvalidOperationException($"addNode: Node {nodeId} already exists (GRID 번호 중복).");

        model.Nodes.Add(new Node(nodeId, new Cmb.Core.Geometry.Point3(x, y, z)));
        // 뒤에 오는 무게중심 RBE 의 AllocNodeId 가 이 번호를 다시 쓰지 않게 한다.
        model.SyncIdCounters();
        session.AddedNodeIds.Add(nodeId);
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "addNode",
            nodeId: nodeId,
            details: $"GRID {nodeId} created @ ({x:R},{y:R},{z:R})"));
    }

    private static HashSet<int> ReferencedNodeIds(FeModel model)
    {
        var referenced = new HashSet<int>();
        foreach (var e in model.Elements) { referenced.Add(e.StartNodeId); referenced.Add(e.EndNodeId); }
        foreach (var r in model.Rigids) { referenced.Add(r.IndependentNodeId); referenced.UnionWith(r.DependentNodeIds); }
        foreach (var pm in model.PointMasses) referenced.Add(pm.NodeId);
        return referenced;
    }

    /// <summary>정리 후보 중 아무 요소·RBE·질량에도 안 걸린 것 = 6-DOF 자유 GRID → 제거.</summary>
    internal static void RemoveOrphanCandidates(FeModel model, EditSession session, List<Dictionary<string, object?>> operations)
    {
        if (session.OrphanCandidates.Count == 0) return;
        var referenced = ReferencedNodeIds(model);
        var orphans = session.OrphanCandidates.Where(n => !referenced.Contains(n) && model.FindNode(n) is not null)
            .OrderBy(n => n).ToList();
        if (orphans.Count == 0) return;
        var orphanSet = orphans.ToHashSet();
        model.Nodes.RemoveAll(n => orphanSet.Contains(n.Id));
        operations.Add(EditTrace.Operation(
            "ORPHAN_NODES_REMOVED", "info", intentId: null,
            details: $"{orphans.Count} GRID(s) no longer referenced after edits: {string.Join(",", orphans)}"));
    }

    internal static void ApplyMoveNodes(
        FeModel model,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        int moved = 0;
        foreach (var m in intent.Params.GetProperty("moves").EnumerateArray())
        {
            int nid = m.GetProperty("nodeId").GetInt32();
            TryReadFiniteNumber(m, "x", out double x);
            TryReadFiniteNumber(m, "y", out double y);
            TryReadFiniteNumber(m, "z", out double z);
            int idx = model.Nodes.FindIndex(n => n.Id == nid);
            // 앞선 편집이 그 노드를 지웠다 — 좌표를 조용히 버리면 화면과 다른 모델이 나간다.
            if (idx < 0) throw new InvalidOperationException($"moveNodes: Node {nid} 가 앞선 편집으로 사라졌습니다.");
            var old = model.Nodes[idx];
            model.Nodes[idx] = new Node(nid, new Cmb.Core.Geometry.Point3(x, y, z), old.Tags);
            moved++;
        }
        // 부재 방향 벡터는 그대로 — BdfWriter 가 BeamOrientation.Resolve 로 새 축 기준으로 정리한다.
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "moveNodes",
            details: $"{moved} GRID(s) moved"));
    }

    internal static void ApplyAddElement(
        FeModel model,
        EditIntent intent,
        EditSession session,
        List<Dictionary<string, object?>> operations)
    {
        var p = intent.Params;
        int eid = p.GetProperty("elementId").GetInt32();
        int a = p.GetProperty("startNode").GetInt32();
        int b = p.GetProperty("endNode").GetInt32();
        int pid = p.GetProperty("propertyId").GetInt32();
        if (p.TryGetProperty("newProperty", out var np) && np.ValueKind == JsonValueKind.Object && model.FindSection(pid) is null)
        {
            var kind = Enum.Parse<BeamSectionKind>(np.GetProperty("kind").GetString()!);
            var dims = np.GetProperty("dims").EnumerateArray().Select(d => d.GetDouble()).ToArray();
            model.Sections.Add(new BeamSection(pid, kind, dims, np.GetProperty("materialId").GetInt32()));
        }
        // 방향 벡터 = 기본값(UnitZ) — BdfWriter 의 BeamOrientation.Resolve 가 엔진 기본 규칙과 같은 v 를 낸다.
        model.Elements.Add(new BeamElement(eid, a, b, pid, EntityCategory.Structure, Cmb.Core.Geometry.Vector3.UnitZ, "viewer-added"));
        model.SyncIdCounters();
        session.AddedElementIds.Add(eid);
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "addElement",
            details: $"CBEAM {eid} created (N{a}-N{b}, PID {pid})"));
    }

    internal static void ApplyDeleteElements(
        FeModel model,
        EditIntent intent,
        EditSession session,
        List<Dictionary<string, object?>> operations)
    {
        var ids = ReadIntList(intent.Params, "elementIds").ToHashSet();
        bool removeOrphans = intent.Params.TryGetProperty("removeOrphanNodes", out var ro) && ro.ValueKind == JsonValueKind.True;
        var removed = model.Elements.Where(e => ids.Contains(e.Id)).ToList();
        foreach (var e in removed)
        {
            model.Elements.Remove(e);
            if (removeOrphans) { session.OrphanCandidates.Add(e.StartNodeId); session.OrphanCandidates.Add(e.EndNodeId); }
        }
        // 앞선 그룹 삭제가 이미 지운 요소는 건너뛴다(결과 동일).
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "deleteElements",
            details: $"{removed.Count}/{ids.Count} element(s) removed" + (removeOrphans ? ", orphan nodes cleaned at end" : "")));
    }

    internal static void ApplyDeleteMasses(
        FeModel model,
        EditIntent intent,
        EditSession session,
        List<Dictionary<string, object?>> operations)
    {
        var ids = ReadIntList(intent.Params, "massIds").ToHashSet();
        var removed = model.PointMasses.Where(pm => ids.Contains(pm.Id)).ToList();
        foreach (var pm in removed)
        {
            model.PointMasses.Remove(pm);
            session.OrphanCandidates.Add(pm.NodeId);
        }
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "deleteMasses",
            details: $"{removed.Count}/{ids.Count} mass(es) removed"));
    }

    internal static bool TryReadIntList(JsonElement p, string name, out List<int> values)
    {
        values = new List<int>();
        if (!p.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int v)) return false;
            values.Add(v);
        }
        values = values.Distinct().ToList();
        return true;
    }

    internal static List<int> ReadIntList(JsonElement p, string name) =>
        TryReadIntList(p, name, out var values) ? values : new List<int>();

    /// <summary>addNode 로 만들고 아무 데도 연결 안 한 GRID — 지우지 않고 알리기만 한다(의도적으로 미리 만들 수도 있다).</summary>
    internal static void ReportUnboundAddedNodes(FeModel model, EditSession session, List<Dictionary<string, object?>> operations)
    {
        if (session.AddedNodeIds.Count == 0) return;
        var referenced = ReferencedNodeIds(model);
        var unbound = session.AddedNodeIds.Where(n => !referenced.Contains(n)).OrderBy(n => n).ToList();
        if (unbound.Count > 0)
            operations.Add(EditTrace.Operation(
                "UNBOUND_ADDED_NODES", "warning", intentId: null,
                kind: "addNode",
                details: $"아무 요소/RBE 에도 연결되지 않은 신규 GRID: {string.Join(",", unbound)}"));
    }

    internal static List<int> FindDroppedIntentNodes(FeModel model, EditSession session)
    {
        var existing = model.Nodes.Select(n => n.Id).ToHashSet();
        var bound = new HashSet<int>();
        foreach (var r in model.Rigids) { bound.Add(r.IndependentNodeId); bound.UnionWith(r.DependentNodeIds); }
        return session.IntentNodes
            .Where(n => !session.ExplicitlyDeletedNodes.Contains(n) && (!existing.Contains(n) || !bound.Contains(n)))
            .OrderBy(n => n)
            .ToList();
    }

    internal static void ApplyDeleteGroup(
        FeModel model,
        ConnectivityGroupDto group,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        var nodeSet = group.NodeIds.ToHashSet();
        var elemSet = group.ElementIds.ToHashSet();
    
        int nodeRemoved = model.Nodes.RemoveAll(n => nodeSet.Contains(n.Id));
        int elemRemoved = model.Elements.RemoveAll(e =>
            elemSet.Contains(e.Id) || nodeSet.Contains(e.StartNodeId) || nodeSet.Contains(e.EndNodeId));
    
        int rbeRemoved = 0;
        foreach (var rigid in model.Rigids.ToList())
        {
            bool indepIn = nodeSet.Contains(rigid.IndependentNodeId);
            var removedDeps = rigid.DependentNodeIds.Where(nodeSet.Contains).ToList();
            if (indepIn)
            {
                model.Rigids.Remove(rigid);
                rbeRemoved++;
                if (removedDeps.Count != rigid.DependentNodeIds.Count)
                    operations.Add(EditTrace.Operation(
                        "BROKEN_RBE_REMOVED", "warning", intent.Id,
                        rbeId: rigid.Id,
                        reason: "GN in deleted set, some GM alive"));
                continue;
            }
    
            if (removedDeps.Count == 0) continue;
            var newDeps = rigid.DependentNodeIds.Where(d => !nodeSet.Contains(d)).ToList();
            model.Rigids.Remove(rigid);
            if (newDeps.Count > 0)
            {
                model.Rigids.Add(new RigidElement(
                    rigid.Id,
                    rigid.IndependentNodeId,
                    newDeps,
                    rigid.Remark,
                    rigid.SourceName,
                    rigid.Cm,
                    rigid.DetailRemark));
            }
            else
            {
                rbeRemoved++;
            }
    
            operations.Add(EditTrace.Operation(
                "RBE_DEP_TRIMMED", "info", intent.Id,
                rbeId: rigid.Id,
                removedGM: removedDeps));
        }
    
        int massRemoved = 0;
        foreach (var pm in model.PointMasses.ToList())
        {
            if (!nodeSet.Contains(pm.NodeId)) continue;
            model.PointMasses.Remove(pm);
            massRemoved++;
            operations.Add(EditTrace.Operation(
                "MASS_REMOVED", "info", intent.Id,
                massId: pm.Id,
                nodeId: pm.NodeId));
        }
    
        PurgeDanglingReferences(model, operations, intent.Id);
    
        int groupId = intent.Params.GetProperty("groupId").GetInt32();
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "deleteGroup",
            details: $"{nodeRemoved} GRIDs / {elemRemoved} BEAMs / {rbeRemoved} RBE2s / {massRemoved} masses removed (groupId={groupId})"));
    }
    
    internal static void ApplyAddRigid(
        FeModel model,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        var p = intent.Params;
    
        // centroid("spider") 모드면 전용 처리로 분기 (기존 2-노드 경로는 아래 그대로).
        if (p.TryGetProperty("createIndependentAtCentroid", out var centProp) &&
            centProp.ValueKind == JsonValueKind.True)
        {
            ApplyAddRigidCentroid(model, intent, operations);
            return;
        }
    
        int indep = p.GetProperty("independentNode").GetInt32();
        var deps0 = p.GetProperty("dependentNodes")
            .EnumerateArray()
            .Select(x => x.GetInt32())
            .Where(x => x != indep)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        string remark = p.TryGetProperty("remark", out var remarkProp) && remarkProp.ValueKind == JsonValueKind.String
            ? remarkProp.GetString() ?? ""
            : "";
        string cm = p.TryGetProperty("cm", out var cmProp) && cmProp.ValueKind == JsonValueKind.String
            ? cmProp.GetString() ?? "123456"
            : "123456";
        if (string.IsNullOrWhiteSpace(cm)) cm = "123456";
    
        // 정확 중복(동일 GN + 동일 GM 집합) skip — 기존 동작 보존(원본 선택 기준으로 판정).
        foreach (var rigid in model.Rigids)
        {
            if (rigid.IndependentNodeId == indep &&
                rigid.DependentNodeIds.OrderBy(x => x).SequenceEqual(deps0))
            {
                operations.Add(EditTrace.Operation(
                    "DUPLICATE_RIGID_SKIPPED", "warning", intent.Id,
                    details: $"existing RBE2 {rigid.Id} has same GN/GM"));
                return;
            }
        }
    
        // 한 노드가 두 RBE2 의 GM 이면 Nastran "종속 DOF 중복 정의" FATAL —
        // 이미 다른 RBE2 의 GM 으로 잡혀 있는 노드는 새 RBE2 의 GM 에서 제외한다.
        // (ApplyAddRigidCentroid 와 동일한 불변식. 겹침이 없는 유효 편집은 제외 대상이 없어 동작 불변.)
        var occupied = model.Rigids.SelectMany(r => r.DependentNodeIds).ToHashSet();
        var deps = deps0.Where(d => !occupied.Contains(d)).ToList();
        int excluded = deps0.Count - deps.Count;
    
        if (deps.Count == 0)
        {
            operations.Add(EditTrace.Operation(
                "INTENT_SKIPPED", "warning", intent.Id,
                kind: "addRigid",
                details: $"모든 종속 노드가 이미 다른 RBE2 의 GM 임(deps0={deps0.Count}, excluded={excluded}). 생성 생략."));
            return;
        }
    
        int newId = EditModelHelpers.NextUnusedElementId(model);
        model.Rigids.Add(new RigidElement(newId, indep, deps, remark, "viewer-added", cm));
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "addRigid",
            details: $"RBE2 {newId} created (remark={remark}, GN={indep}, GM=[{string.Join(",", deps)}], CM={cm})"
                     + (excluded > 0 ? $" — excluded {excluded} node(s) already GM of another RBE2" : "")));
    }
    
    // addRigid CENTROID("spider") 모드:
    // 선택된 종속 노드들의 산술 평균지점(평균지점)에 새 독립 GRID 노드를 만들고,
    // 그 새 노드를 GN(독립)으로, 선택 노드들을 GM(종속)으로 하는 RBE2 를 생성한다.
    // JS 프리뷰와 결과가 일치해야 하므로 알고리즘을 그대로 따른다:
    //   1) deps0 = dependentNodes(중복 제거, 순서 보존)
    //   2) occupied = 기존 RBE 의 임의 GM 으로 이미 잡혀 있는 노드 집합
    //   3) deps = occupied 제외(한 노드가 두 RBE2 의 GM 이면 Nastran FATAL) — 순서 보존
    //   4) deps.Count < 2 → 아무것도 만들지 않고 skip 기록 후 반환
    //   5) center = 필터된 deps 좌표의 산술 평균(평균지점). independentCoord 가 있으면 그 값으로 대체
    //   6) newNodeId = AllocNodeId() (단조 증가 — 한 run 의 다중 centroid intent 끼리도 충돌 없음)
    //   7) RBE2 생성 (GN=새 노드, GM=deps, CM=cm)
    //   8) mass>0 이면 새 노드에 CONM2(PointMass) 추가
    //   9) EditTrace INTENT_APPLIED 기록
    internal static void ApplyAddRigidCentroid(
        FeModel model,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        var p = intent.Params;
    
        string cm = p.TryGetProperty("cm", out var cmProp) && cmProp.ValueKind == JsonValueKind.String
            ? cmProp.GetString() ?? "123456"
            : "123456";
        if (string.IsNullOrWhiteSpace(cm)) cm = "123456";
    
        var deps0 = p.GetProperty("dependentNodes")
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Number)
            .Select(x => x.GetInt32())
            .Distinct()
            .ToList();
    
        var occupied = model.Rigids.SelectMany(r => r.DependentNodeIds).ToHashSet();
        var deps = deps0.Where(d => !occupied.Contains(d)).ToList();
        int excluded = deps0.Count - deps.Count;
    
        if (deps.Count < 2)
        {
            operations.Add(EditTrace.Operation(
                "INTENT_SKIPPED", "warning", intent.Id,
                kind: "addRigid(centroid)",
                details: $"GM {deps.Count}개(<2) — 선택 노드 대부분이 이미 다른 RBE2 의 GM 임" +
                         $"(deps0={deps0.Count}, excluded={excluded}). 생성 생략."));
            return;
        }
    
        double cx, cy, cz;
        if (TryReadIndependentCoord(p, out double ox, out double oy, out double oz))
        {
            cx = ox; cy = oy; cz = oz;
        }
        else
        {
            double sx = 0, sy = 0, sz = 0;
            foreach (int d in deps)
            {
                var pos = model.FindNode(d)!.Position;
                sx += pos.X; sy += pos.Y; sz += pos.Z;
            }
            cx = sx / deps.Count;
            cy = sy / deps.Count;
            cz = sz / deps.Count;
        }
    
        int newNodeId = model.AllocNodeId();
        model.Nodes.Add(new Node(newNodeId, new Cmb.Core.Geometry.Point3(cx, cy, cz)));
    
        int rbeId = EditModelHelpers.NextUnusedElementId(model);
        model.Rigids.Add(new RigidElement(rbeId, newNodeId, deps, "centroid-spider", "viewer-added", cm));
    
        double mass = 0;
        bool hasMass = p.TryGetProperty("mass", out var massProp)
            && massProp.ValueKind == JsonValueKind.Number
            && massProp.TryGetDouble(out mass)
            && mass > 0;
        int? massEid = null;
        if (hasMass)
        {
            int eid = EditModelHelpers.NextUnusedElementId(model);
            model.PointMasses.Add(new PointMass(eid, newNodeId, mass, "viewer-added"));
            massEid = eid;
        }
    
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "addRigid(centroid)",
            rbeId: rbeId,
            nodeId: newNodeId,
            massId: massEid,
            details: $"RBE2 {rbeId} created (GN={newNodeId} @ ({cx:R},{cy:R},{cz:R}), " +
                     $"GM=[{string.Join(",", deps)}] count={deps.Count}, excluded={excluded}, CM={cm}" +
                     (hasMass ? $", mass={mass:R}t CONM2={massEid}" : "") + ")"));
    }
    
    // independentCoord(평균지점 override)를 읽는다. x/y/z 모두 유한수일 때만 true.
    internal static bool TryReadIndependentCoord(JsonElement p, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (!p.TryGetProperty("independentCoord", out var coord) || coord.ValueKind != JsonValueKind.Object)
            return false;
        return TryReadFiniteNumber(coord, "x", out x)
            && TryReadFiniteNumber(coord, "y", out y)
            && TryReadFiniteNumber(coord, "z", out z);
    }
    
    /// <summary>rotateModel.params.pivot {x,y,z} — Studio 가 기록한 회전 기준점. 없거나 불완전하면 null.</summary>
    internal static Cmb.Core.Geometry.Point3? TryReadPivot(EditIntent intent)
    {
        if (!intent.Params.TryGetProperty("pivot", out var pivot) || pivot.ValueKind != JsonValueKind.Object)
            return null;
        return TryReadFiniteNumber(pivot, "x", out double x)
            && TryReadFiniteNumber(pivot, "y", out double y)
            && TryReadFiniteNumber(pivot, "z", out double z)
            ? new Cmb.Core.Geometry.Point3(x, y, z)
            : null;
    }

    internal static bool TryReadFiniteNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out value)
            && double.IsFinite(value);
    }
    
    internal static void ApplyEmptyPipeFluid(
        FeModel model,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        var materialIds = intent.Params.GetProperty("materialIds")
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .Distinct()
            .ToHashSet();
        double targetRho = intent.Params.GetProperty("targetRho").GetDouble();
    
        int changed = 0;
        for (int i = 0; i < model.Materials.Count; i++)
        {
            var material = model.Materials[i];
            if (!materialIds.Contains(material.Id)) continue;
            if (Math.Abs(material.Rho - targetRho) <= Math.Abs(targetRho) * 1e-12) continue;
    
            model.Materials[i] = material with { Rho = targetRho };
            changed++;
        }
    
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "emptyPipeFluid",
            details: $"{changed}/{materialIds.Count} Pipe materials changed to rho={targetRho:R}"));
    }
    
    internal static void ApplyRotateModel(
        FeModel model,
        EditIntent intent,
        List<Dictionary<string, object?>> operations)
    {
        char axis = intent.Params.GetProperty("axis").GetString()![0];
        double angleDeg = intent.Params.GetProperty("angleDeg").GetDouble();
    
        // pivot = Studio 가 intent 에 기록한 회전 기준점(params.pivot). 없으면(옛 intent) 현재 모델 무게중심.
        // Studio 는 클릭 시점의 전체 모델 CoG 로 화면을 돌리는데, 엔진이 '앞선 편집(그룹 삭제·유체 비움)
        // 적용 후 CoG' 로 돌리면 출력 BDF 좌표가 화면과 평행이동만큼 어긋났다(2026-10-02 정합성 검토).
        // nastran_bridge(rotate_model_data)도 같은 pivot 을 쓴다.
        var cog = TryReadPivot(intent) ?? MassPropertiesAnalyzer.Analyze(model).CenterOfGravity;
        int rotated = model.RotateAboutPivot(axis, angleDeg, cog);
    
        operations.Add(EditTrace.Operation(
            "INTENT_APPLIED", "info", intent.Id,
            kind: "rotateModel",
            details: $"axis={axis} angleDeg={angleDeg:R} pivotCoG=({cog.X:F1},{cog.Y:F1},{cog.Z:F1}) nodesRotated={rotated}"));
    }
    
    internal static void PurgeDanglingReferences(
        FeModel model,
        List<Dictionary<string, object?>> operations,
        string? intentId)
    {
        var nodeIds = model.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var elem in model.Elements.ToList())
        {
            if (nodeIds.Contains(elem.StartNodeId) && nodeIds.Contains(elem.EndNodeId)) continue;
            model.Elements.Remove(elem);
            operations.Add(EditTrace.Operation(
                "DANGLING_REF_PURGED", "info", intentId,
                card: $"CBEAM {elem.Id}",
                details: $"removed element referencing missing node ({elem.StartNodeId}, {elem.EndNodeId})"));
        }
    
        foreach (var rigid in model.Rigids.ToList())
        {
            if (!nodeIds.Contains(rigid.IndependentNodeId))
            {
                model.Rigids.Remove(rigid);
                operations.Add(EditTrace.Operation(
                    "DANGLING_REF_PURGED", "info", intentId,
                    card: $"RBE2 {rigid.Id}",
                    removedGID: rigid.IndependentNodeId));
                continue;
            }
    
            var deps = rigid.DependentNodeIds.Where(nodeIds.Contains).ToList();
            if (deps.Count == rigid.DependentNodeIds.Count) continue;
    
            model.Rigids.Remove(rigid);
            if (deps.Count > 0)
            {
                model.Rigids.Add(new RigidElement(
                    rigid.Id,
                    rigid.IndependentNodeId,
                    deps,
                    rigid.Remark,
                    rigid.SourceName,
                    rigid.Cm,
                    rigid.DetailRemark));
            }
            operations.Add(EditTrace.Operation(
                "DANGLING_REF_PURGED", "info", intentId,
                card: $"RBE2 {rigid.Id}",
                details: "trimmed dependent nodes referencing missing GRIDs"));
        }
    
        foreach (var pm in model.PointMasses.ToList())
        {
            if (nodeIds.Contains(pm.NodeId)) continue;
            model.PointMasses.Remove(pm);
            operations.Add(EditTrace.Operation(
                "DANGLING_REF_PURGED", "info", intentId,
                card: $"CONM2 {pm.Id}",
                removedGID: pm.NodeId));
        }
    }
}
