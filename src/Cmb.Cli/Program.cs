using System.CommandLine;
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

var rootCommand = new RootCommand("HiTessModelBuilder — AM CSV → Nastran BDF 변환기");
var rootApplyEditIntentOption = new Option<DirectoryInfo?>("--apply-edit-intent")
{
    Description = "viewer *_edit.json을 적용할 결과 폴더",
};
var rootApplyEditOutOption = new Option<DirectoryInfo?>("--out")
{
    Description = "편집 결과 출력 폴더 (기본: <folder>/edited)",
};
var rootApplyEditStrictOption = new Option<bool>("--strict")
{
    Description = "viewer warning intent도 error로 처리",
};
rootCommand.Add(rootApplyEditIntentOption);
rootCommand.Add(rootApplyEditOutOption);
rootCommand.Add(rootApplyEditStrictOption);
rootCommand.Add(BuildParseCommand());
rootCommand.Add(BuildBuildRawCommand());
rootCommand.Add(BuildBuildFullCommand());
rootCommand.Add(BuildApplyEditIntentCommand());
rootCommand.SetAction(parseResult =>
{
    var folder = parseResult.GetValue(rootApplyEditIntentOption);
    if (folder is null)
    {
        Console.Error.WriteLine("[오류] 명령을 지정하세요. 예: build-full 또는 --apply-edit-intent <폴더>");
        return 1;
    }

    return ApplyEditIntent(
        folder,
        parseResult.GetValue(rootApplyEditOutOption),
        parseResult.GetValue(rootApplyEditStrictOption));
});
return rootCommand.Parse(args).Invoke();

// ── 공통 옵션 팩토리 ─────────────────────────────────────────────────────────

static (Option<DirectoryInfo?> input, Option<FileInfo?> stru, Option<FileInfo?> pipe, Option<FileInfo?> equip)
    MakeCsvOptions()
{
    return (
        new Option<DirectoryInfo?>("--input")  { Description = "CSV 폴더 (struData/pipeData/equpData 자동 탐색)" },
        new Option<FileInfo?>("--stru")         { Description = "Structure CSV 파일 경로" },
        new Option<FileInfo?>("--pipe")         { Description = "Pipe CSV 파일 경로" },
        new Option<FileInfo?>("--equip")        { Description = "Equipment CSV 파일 경로" }
    );
}

// ── CSV 로드 + baseName + csvDir 결정 ─────────────────────────────────────────

static (RawDesignData? data, string baseName, string csvDir, CsvInputPaths paths, string? error) LoadCsv(
    DirectoryInfo? input, FileInfo? stru, FileInfo? pipe, FileInfo? equip)
{
    var loader = new CsvDesignLoader();

    if (stru is not null || pipe is not null || equip is not null)
    {
        var firstFile = (stru ?? pipe ?? equip)!;
        var baseName  = firstFile.Name.Replace(".csv", "", StringComparison.OrdinalIgnoreCase);
        var csvDir    = firstFile.DirectoryName ?? ".";
        var paths = new CsvInputPaths(stru?.FullName, pipe?.FullName, equip?.FullName);
        return (loader.Load(paths.StructurePath, paths.PipePath, paths.EquipmentPath), baseName, csvDir, paths, null);
    }

    if (input is not null)
    {
        if (!input.Exists)
            return (null, "", "", new CsvInputPaths(null, null, null), $"[오류] 입력 폴더를 찾을 수 없습니다: {input.FullName}");

        var files = Directory.GetFiles(input.FullName, "*.csv");
        var paths = new CsvInputPaths(
            MatchFirst(files, "struData", "structure", "support", "supt"),
            MatchFirst(files, "pipeData", "pipe"),
            MatchFirst(files, "equpData", "equip"));
        return (loader.Load(paths.StructurePath, paths.PipePath, paths.EquipmentPath), input.Name, input.FullName, paths, null);
    }

    return (null, "", "", new CsvInputPaths(null, null, null), "[오류] --input 폴더 또는 --stru/--pipe/--equip 파일 중 하나 이상을 지정하세요.");
}

static string? MatchFirst(string[] files, params string[] patterns)
{
    foreach (var pat in patterns)
    {
        var hit = files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(pat, StringComparison.OrdinalIgnoreCase) ||
            f.Contains(pat, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
    }
    return null;
}

// ── 최종 산출물 파일명 — CSV 파일명에서 struData/pipeData/equpData 토큰 제거 ──
// 예) "3468-35030-struData-A453412_20260108.csv" → "3468-35030-A453412_20260108"
static string DeriveDesignName(string fallback, CsvInputPaths paths)
{
    string? sample = paths.StructurePath ?? paths.PipePath ?? paths.EquipmentPath;
    if (string.IsNullOrEmpty(sample)) return fallback;

    string name = Path.GetFileNameWithoutExtension(sample);
    foreach (var token in new[] { "struData", "pipeData", "equpData" })
        name = StripDataToken(name, token);

    name = name.Trim('-', '_', ' ');
    return string.IsNullOrEmpty(name) ? fallback : name;
}

// ── --ubolt-full-fix 적용: 모델의 UBOLT RBE2 cm을 "123456"으로 일괄 교체 ──
// RigidElement.Cm 은 immutable 이므로 동일 ID/Indep/Dep 으로 새 인스턴스를 만들어 리스트를 교체한다.
// 최종 BDF/JSON 직전에 호출하여 두 산출물이 동일한 cm을 보고하도록 한다 (phase JSON 은 보존).
static int ApplyUboltFullDof(FeModel model)
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
static int ApplySpcTag(FeModel model, HashSet<int> spcNodeIdSet)
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
static int PatchPhaseJsonsWithSpcTag(string outDir, HashSet<int> spcNodeIdSet)
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
        catch { continue; }

        System.Text.Json.Nodes.JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(text); }
        catch { continue; }
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

static string StripDataToken(string name, string token)
{
    int idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) return name;

    int start = idx;
    int end   = idx + token.Length;
    // 인접 하이픈 1개 흡수: "-token-" → 한쪽만 제거하여 양 옆 식별자가 자연스럽게 이어지도록
    if (start > 0 && name[start - 1] == '-')      start--;
    else if (end < name.Length && name[end] == '-') end++;

    return name[..start] + name[end..];
}

// ── 타임스탬프 출력 폴더 생성 ─────────────────────────────────────────────────

static string MakeOutputDir(string csvDir)
{
    var ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var dir = Path.Combine(csvDir, ts);
    Directory.CreateDirectory(dir);
    return dir;
}

// ── parse ────────────────────────────────────────────────────────────────────

static Command BuildParseCommand()
{
    var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();

    var cmd = new Command("parse", "CSV를 파싱하여 raw JSON 덤프 생성");
    cmd.Add(inputOption);
    cmd.Add(struOption);
    cmd.Add(pipeOption);
    cmd.Add(equipOption);

    cmd.SetAction(parseResult =>
    {
        var (data, baseName, csvDir, _, error) = LoadCsv(
            parseResult.GetValue(inputOption),
            parseResult.GetValue(struOption),
            parseResult.GetValue(pipeOption),
            parseResult.GetValue(equipOption));

        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var outDir  = MakeOutputDir(csvDir);

        foreach (var skip in data!.Skips)
            Console.Error.WriteLine($"[SKIP] {skip.Kind} line {skip.LineNumber} '{skip.Name}': {skip.Reason}");

        var outFile = Path.Combine(outDir, $"{baseName}.raw.json");
        File.WriteAllText(outFile, data.ToRawJson(baseName));

        Console.WriteLine($"완료: 구조물 {data.Beams.Count}개  배관 {data.Pipes.Count}개  장비 {data.Equips.Count}개");
        Console.WriteLine($"출력: {outFile}");

        if (data.Skips.Count > 0)
            Console.Error.WriteLine($"[경고] 파싱 실패 {data.Skips.Count}행");

        return 0;
    });

    return cmd;
}

// ── build-raw ────────────────────────────────────────────────────────────────

static Command BuildBuildRawCommand()
{
    var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();

    var cmd = new Command("build-raw", "CSV → FeModel 변환 (알고리즘 미적용 원본 모델)");
    cmd.Add(inputOption);
    cmd.Add(struOption);
    cmd.Add(pipeOption);
    cmd.Add(equipOption);

    cmd.SetAction(parseResult =>
    {
        var (data, baseName, csvDir, _, error) = LoadCsv(
            parseResult.GetValue(inputOption),
            parseResult.GetValue(struOption),
            parseResult.GetValue(pipeOption),
            parseResult.GetValue(equipOption));

        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var outDir = MakeOutputDir(csvDir);

        foreach (var skip in data!.Skips)
            Console.Error.WriteLine($"[SKIP] {skip.Kind} line {skip.LineNumber} '{skip.Name}': {skip.Reason}");

        var model = new FeModelBuilder().Build(data);
        var connectivity = ConnectivityAnalyzer.Analyze(model);
        var healthMetrics = HealthMetricsAnalyzer.Analyze(model, null, new Tolerances());

        var jsonFile = Path.Combine(outDir, $"{baseName}.initial.json");
        File.WriteAllText(jsonFile, model.ToJson(phase: "B", stageName: "initial",
            connectivity: connectivity, healthMetrics: healthMetrics));

        var bdfFile = Path.Combine(outDir, $"{baseName}.initial.bdf");
        using (var writer = new StreamWriter(bdfFile))
            BdfWriter.Write(model, writer);

        Console.WriteLine($"완료: 노드 {model.Nodes.Count}개  빔 {model.Elements.Count}개  단면 {model.Sections.Count}개  집중질량 {model.PointMasses.Count}개");
        Console.WriteLine($"폴더: {outDir}");
        Console.WriteLine($"JSON: {jsonFile}");
        Console.WriteLine($"BDF:  {bdfFile}");

        if (data.Skips.Count > 0)
            Console.Error.WriteLine($"[경고] 파싱 실패 {data.Skips.Count}행");

        return 0;
    });

    return cmd;
}

// ── build-full ───────────────────────────────────────────────────────────────

static Command BuildBuildFullCommand()
{
    var stopatOption = new Option<string?>("--stopat")
    {
        Description = "지정 스테이지 이름까지만 실행 (예: NodeEquivalence)",
    };
    var verboseDumpOption = new Option<bool>("--verbose-dump")
    {
        Description = "모든 sub-stage를 detail/ 하위 폴더에 추가 dump (디버깅용)",
    };
    var runNastranOption = new Option<bool>("--run-nastran")
    {
        Description = "01.final.bdf에 GRAV(-9800) + leg 하부 SPC1 추가 후 Nastran 실행",
    };
    var nastranPathOption = new Option<string>("--nastran-path")
    {
        Description = "Nastran 실행 파일 경로 (기본: C:\\MSC.Software\\MSC_Nastran\\20131\\bin\\nastran.exe)",
        DefaultValueFactory = _ => @"C:\MSC.Software\MSC_Nastran\20131\bin\nastran.exe",
    };
    var legZTolOption = new Option<double>("--leg-z-tol")
    {
        Description = "Leg 하부 SPC 검출 허용 Z 범위 (mm, 기본 500)",
        DefaultValueFactory = _ => 500.0,
    };
    var uboltFullFixOption = new Option<bool>("--ubolt-full-fix")
    {
        Description = "모든 UBOLT(BOX + pipe) RBE2의 cm을 \"123456\"으로 강제 (mechanism 회피, 최종 BDF/JSON 모두 반영)",
    };
    var meshSizeOption = new Option<double?>("--mesh-size")
    {
        Description = "최종 메쉬 최대 길이 (mm, Structure/Pipe 모두에 적용). 지정 시 모든 최종 segment가 이 값 미만이 되도록 분할",
    };
    var meshSizeStructureOption = new Option<double?>("--mesh-size-structure")
    {
        Description = "최종 Structure 메쉬 최대 길이 (mm). --mesh-size 보다 우선",
    };
    var meshSizePipeOption = new Option<double?>("--mesh-size-pipe")
    {
        Description = "최종 Pipe 메쉬 최대 길이 (mm). --mesh-size 보다 우선",
    };

    var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();

    var cmd = new Command("build-full", "CSV → FeModel → 파이프라인 스테이지 실행 (phase별 JSON + BDF 출력)");
    cmd.Add(inputOption);
    cmd.Add(struOption);
    cmd.Add(pipeOption);
    cmd.Add(equipOption);
    cmd.Add(stopatOption);
    cmd.Add(verboseDumpOption);
    cmd.Add(runNastranOption);
    cmd.Add(nastranPathOption);
    cmd.Add(legZTolOption);
    cmd.Add(uboltFullFixOption);
    cmd.Add(meshSizeOption);
    cmd.Add(meshSizeStructureOption);
    cmd.Add(meshSizePipeOption);

    cmd.SetAction(parseResult =>
    {
        var (data, baseName, csvDir, inputPaths, error) = LoadCsv(
            parseResult.GetValue(inputOption),
            parseResult.GetValue(struOption),
            parseResult.GetValue(pipeOption),
            parseResult.GetValue(equipOption));

        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var stopAt       = parseResult.GetValue(stopatOption);
        bool verboseDump = parseResult.GetValue(verboseDumpOption);
        bool runNastran  = parseResult.GetValue(runNastranOption);
        string nastranPath = parseResult.GetValue(nastranPathOption)!;
        double legZTol   = parseResult.GetValue(legZTolOption);
        bool uboltFullFix = parseResult.GetValue(uboltFullFixOption);
        double? meshAll      = parseResult.GetValue(meshSizeOption);
        double? meshStruOpt  = parseResult.GetValue(meshSizeStructureOption);
        double? meshPipeOpt  = parseResult.GetValue(meshSizePipeOption);
        double? finalMeshStructure = meshStruOpt ?? meshAll;
        double? finalMeshPipe      = meshPipeOpt ?? meshAll;
        if ((finalMeshStructure.HasValue && finalMeshStructure.Value <= 0) ||
            (finalMeshPipe.HasValue && finalMeshPipe.Value <= 0))
        {
            Console.Error.WriteLine("[오류] mesh-size 값은 양수여야 합니다.");
            return 1;
        }
        var outDir       = MakeOutputDir(csvDir);
        Console.WriteLine($"출력 폴더: {outDir}");
        if (finalMeshStructure.HasValue || finalMeshPipe.HasValue)
        {
            string struMesh = finalMeshStructure.HasValue ? $"{finalMeshStructure}mm" : "off";
            string pipeMesh = finalMeshPipe.HasValue ? $"{finalMeshPipe}mm" : "off";
            Console.WriteLine($"final mesh size: Structure={struMesh}, Pipe={pipeMesh}");
        }

        foreach (var skip in data!.Skips)
            Console.Error.WriteLine($"[SKIP] {skip.Kind} line {skip.LineNumber} '{skip.Name}': {skip.Reason}");

        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger  = loggerFactory.CreateLogger("Pipeline");
        var options = new RunOptions(new Tolerances(), StopAfterStage: stopAt);

        var model = new FeModelBuilder().Build(data, options.PipePidBase);
        var inputAudit = InputAuditJson.Build(inputPaths, data);
        var inputAuditFile = Path.Combine(outDir, "00_InputAudit.json");
        File.WriteAllText(inputAuditFile, inputAudit.ToJson());
        Console.WriteLine($"입력 감사: {inputAuditFile}");

        // 1단계 CSV 입력 검증 — 데이터 오류(예: 배관 outDia<=0) 를 log 로 남긴다.
        // 모델 변환은 그대로 진행하되, 조용히 누락되지 않도록 오류 행을 stderr 로 보고한다.
        var errorRows = inputAudit.RowAudit.Where(r => r.Status == "error").ToList();
        if (errorRows.Count > 0)
        {
            Console.Error.WriteLine($"[오류] CSV 입력 검증: 데이터 오류 {errorRows.Count}행 (변환 시 형상 누락 가능)");
            foreach (var grp in errorRows.GroupBy(r => r.ReasonCode).OrderBy(g => g.Key))
                Console.Error.WriteLine($"  - {grp.Key}: {grp.Count()}행");
            foreach (var r in errorRows.Take(20))
                Console.Error.WriteLine($"  [오류] {r.Kind} line {r.PhysicalLineNumber} '{r.Name}': {r.ReasonCode} — {r.Reason}");
            if (errorRows.Count > 20)
                Console.Error.WriteLine($"  … 그 외 {errorRows.Count - 20}행 (전체는 00_InputAudit.json 참조)");
        }
        var stageSummary = new StageSummaryDocument();
        var stageSummaryFile = Path.Combine(outDir, "00_StageSummary.json");
        var initialUboltDiagnostics = PreconnectInitialUbolts(model, options, logger);

        IPipelineStage[] stages =
        [
            new SanityPreprocessStage(),
            new MeshingStage(),
            new NodeEquivalenceStage(),
            new IntersectionStage(),
            new DanglingShortRemoveStage(),
            new CollinearNodeMergeStage(),
            new ConnectivityConvergenceStage(),      // Extend/Cleanup/GroupConnect 수렴 루프 → Connectivity 완료
            new PointMassReattachStage(),            // 공중에 뜬 PointMass 노드 → 최근접 pipe 노드로 재연결
            new BoxUboltConnectionStage(),           // BOX U-bolt → 상/하/좌/우 구조 부재 RBE2 연결
            new SplitByExistingNodesStage(),         // BOX U-bolt snap 노드 → 구조 부재 분할
            new RigidProximityMergeStage(),          // 50mm 이내 인접 RBE2 병합 (pipe-pipe 연결 형성)
            new FreeEndRbeStage(),                   // 잔여 자유단 → 가장 가까운 부재에 RBE2 브릿지
            new SplitByExistingNodesStage(),         // FreeEndRbe projection 노드 → 타겟 부재 분할
            new PreservedFreeEndCleanupStage(),      // 더 이상 free-end가 아닌 보존 RBE 제거
            new UboltAnchorVerticalizeStage(),       // Group 이동 후 UBOLT anchor → support Z수직선 정렬
            new OrphanNodeRemoveStage(),             // 어떤 element/rigid/pm 에도 참조되지 않는 노드 제거
            new FinalMeshSizeStage(finalMeshStructure, finalMeshPipe), // 최종 위치 확정 후 mesh-size 분할
            new FinalMeshProximityConnectStage(),    // FinalMesh 내부 구조 노드 → 근접 구조 부재 보수적 RBE 연결
            new LoopCloseProximityStage(),           // 같은 그룹이지만 위상상 먼 근접 구조 부재 → loop-close RBE2 연결
            new SplitByExistingNodesStage(),         // FinalMeshProximity/LoopClose projection 노드 → 타겟 부재 분할
            new FinalTopologyHealingStage(),         // 최종 mesh 분할 후 기존 노드가 요소 선상에 놓인 연결성 보정
            new NodeEquivalenceStage(),              // 후속 stage가 동일 좌표에 추가한 잉여 노드 최종 정리
            new RbeBeamDeduplicateStage(),           // RBE2 indep↔dep 페어가 CBEAM 양 끝점과 중복되는 dep 제거
            new RedundantBridgeRbeRemoveStage(),     // 자동 생성 RBE2 중 CBEAM connected component 상 이미 연결된 dep 제거
            new RigidSharedNodeMergeStage(),         // 한 노드가 2개 이상 RBE2에 걸린 경우 노드 공유 기준 단일 RBE2 병합
            new FinalValidationStage(),
        ];

        // Phase boundary table: index = 0-based stage index in `stages`
        // null = not a phase boundary (skip dump); value = (phaseName, fileIdx)
        (string Name, int FileIdx)?[] phaseBoundaries =
        [
            null,                      // [0]  SanityPreprocess
            ("Preprocess",   1),       // [1]  Meshing              → Preprocess 완료
            null,                      // [2]  NodeEquivalence
            null,                      // [3]  Intersection
            ("ElementSplit", 2),       // [4]  DanglingShortRemove  → ElementSplit 완료
            ("NodeMerge",    3),       // [5]  CollinearNodeMerge   → NodeMerge 완료
            ("Connectivity", 4),       // [6]  ConnectivityConvergence → Connectivity 완료
            null,                      // [7]  PointMassReattach
            null,                      // [8]  BoxUboltConnection
            null,                      // [9]  SplitByExistingNodes (1st)
            null,                      // [10] RigidProximityMerge
            null,                      // [11] FreeEndRbe
            ("RigidPostProc", 5),      // [12] SplitByExistingNodes (2nd) → RigidPostProc 완료
            null,                      // [13] PreservedFreeEndCleanup
            null,                      // [14] UboltAnchorVerticalize
            null,                      // [15] OrphanNodeRemove (Validation phase 합산)
            null,                      // [16] FinalMeshSize
            null,                      // [17] FinalMeshProximityConnect
            null,                      // [18] LoopCloseProximity
            null,                      // [19] SplitByExistingNodes (FinalMeshProximity/LoopClose)
            null,                      // [20] FinalTopologyHealing
            null,                      // [21] NodeEquivalence (final)
            null,                      // [22] RbeBeamDeduplicate
            null,                      // [23] RedundantBridgeRbeRemove
            null,                      // [24] RigidSharedNodeMerge
            ("Validation",   6),       // [25] FinalValidation      → Validation 완료
        ];

        int idx               = 0;
        int phaseStartElems   = model.Elements.Count;
        int phaseStartNodes   = model.Nodes.Count;
        string prevPhaseStage = "Start";
        var stageHistory      = new List<StageHistoryEntryDto>();
        int phElCreated = 0, phElRemoved = 0, phElSplit = 0;
        int phNdMerged  = 0, phNdMoved  = 0;
        int phNdCreated = 0, phNdRemoved = 0;
        var phaseAccumDiags = new List<DiagnosticDto>();

        string? detailDir = verboseDump ? Path.Combine(outDir, "detail") : null;
        if (detailDir is not null) Directory.CreateDirectory(detailDir);

        var report = PipelineRunner.Run(model, stages, options, logger,
            onStageComplete: (stageName, m, stageDiagnostics, durationMs, delta) =>
            {
                idx++;
                int si = idx - 1;  // 0-based stage index
                var boundary = si < phaseBoundaries.Length ? phaseBoundaries[si] : null;

                var combinedDiags = idx == 1 && initialUboltDiagnostics.Count > 0
                    ? initialUboltDiagnostics.Concat(stageDiagnostics).ToList()
                    : stageDiagnostics;

                var diagDtos = combinedDiags.Select(d => new DiagnosticDto
                {
                    Severity = d.Severity.ToString().ToLowerInvariant(),
                    Code     = d.Code,
                    Message  = d.Message,
                    ElemId   = d.ElementId,
                    NodeId   = d.NodeId,
                }).ToList();

                // Accumulate phase delta
                phElCreated += delta.ElementsCreated;
                phElRemoved += delta.ElementsRemoved;
                phElSplit   += delta.ElementsSplit;
                phNdMerged  += delta.NodesMerged;
                phNdMoved   += delta.NodesMoved;
                phNdCreated += delta.NodesCreated;
                phNdRemoved += delta.NodesRemoved;
                phaseAccumDiags.AddRange(diagDtos);

                // Lightweight stage history (no heavy analysis)
                stageHistory.Add(new StageHistoryEntryDto
                {
                    StageIndex           = idx,
                    Stage                = stageName,
                    Timestamp            = DateTime.UtcNow.ToString("o"),
                    ProcessingDurationMs = durationMs,
                    ElementCount         = m.Elements.Count,
                    NodeCount            = m.Nodes.Count,
                    IssueCount           = new StageHistoryIssueCountDto
                    {
                        Error   = combinedDiags.Count(d => d.Severity == DiagnosticSeverity.Error),
                        Warning = combinedDiags.Count(d => d.Severity == DiagnosticSeverity.Warning),
                        Info    = combinedDiags.Count(d => d.Severity == DiagnosticSeverity.Info),
                    },
                });

                // Console: one-liner per stage
                int de = delta.ElemCountAfter - delta.ElemCountBefore;
                int dn = delta.NodeCountAfter - delta.NodeCountBefore;
                Console.WriteLine($"  [{idx:D2}/{stages.Length}] {stageName,-30} Δelem={de:+#;-#;0} Δnode={dn:+#;-#;0}  {durationMs}ms");

                // Verbose: per-stage dump to detail/
                if (detailDir is not null)
                {
                    var conn2   = ConnectivityAnalyzer.Analyze(m);
                    var dtos2   = ModelDiagnosticsAnalyzer.Analyze(m, options.Tolerances);
                    dtos2.AddRange(diagDtos);
                    var health2 = HealthMetricsAnalyzer.Analyze(m, combinedDiags, options.Tolerances);
                    health2.DiagnosticCounts = CountDiagnostics(dtos2);
                    var stageDeltaDto = new StageDeltasDto
                    {
                        FromStage       = delta.FromStage,       ToStage         = stageName,
                        ElementsCreated = delta.ElementsCreated, ElementsRemoved = delta.ElementsRemoved,
                        ElementsSplit   = delta.ElementsSplit,   NodesMerged     = delta.NodesMerged,
                        NodesMoved      = delta.NodesMoved,      NodesCreated    = delta.NodesCreated,
                        NodesRemoved    = delta.NodesRemoved,    ElemCountBefore = delta.ElemCountBefore,
                        ElemCountAfter  = delta.ElemCountAfter,  NodeCountBefore = delta.NodeCountBefore,
                        NodeCountAfter  = delta.NodeCountAfter,
                    };
                    bool isLastDetail = stages[^1].Name == stageName
                        || (stopAt is not null && string.Equals(stageName, stopAt, StringComparison.OrdinalIgnoreCase));

                    ValidationResultDto? vr2 = null;
                    if (stageName == "FinalValidation")
                    {
                        vr2 = BuildValidationResult(stageDiagnostics, conn2.GroupCount, m);
                    }

                    var dp = Path.Combine(detailDir, $"{idx:D2}_{stageName}");
                    File.WriteAllText(dp + ".json", m.ToJson(phase: "C", stageName: stageName,
                        connectivity: conn2, healthMetrics: health2, stageDiagnostics: dtos2,
                        stageIndex: idx, processingDurationMs: durationMs, stageDeltas: stageDeltaDto,
                        validationResult: vr2, stageHistory: isLastDetail ? stageHistory : null));
                    using (var w = new StreamWriter(dp + ".bdf")) BdfWriter.Write(m, w, pipePidBase: options.PipePidBase);
                    Console.WriteLine($"       → detail/{idx:D2}_{stageName}.*");
                }

                // Phase dump at boundary
                if (boundary.HasValue)
                {
                    var (phaseName, phFileIdx) = boundary.Value;
                    var conn   = ConnectivityAnalyzer.Analyze(m);
                    var pdtos  = ModelDiagnosticsAnalyzer.Analyze(m, options.Tolerances);
                    pdtos.AddRange(phaseAccumDiags);
                    var health = HealthMetricsAnalyzer.Analyze(m, combinedDiags, options.Tolerances);
                    health.DiagnosticCounts = CountDiagnostics(pdtos);
                    var phaseDelta = new StageDeltasDto
                    {
                        FromStage       = prevPhaseStage,    ToStage         = phaseName,
                        ElementsCreated = phElCreated,       ElementsRemoved = phElRemoved,
                        ElementsSplit   = phElSplit,         NodesMerged     = phNdMerged,
                        NodesMoved      = phNdMoved,         NodesCreated    = phNdCreated,
                        NodesRemoved    = phNdRemoved,       ElemCountBefore = phaseStartElems,
                        ElemCountAfter  = m.Elements.Count,  NodeCountBefore = phaseStartNodes,
                        NodeCountAfter  = m.Nodes.Count,
                    };
                    bool isLastPhase = phaseName == "Validation"
                        || (stopAt is not null && string.Equals(stageName, stopAt, StringComparison.OrdinalIgnoreCase));

                    ValidationResultDto? validationResult = null;
                    if (phaseName == "Validation")
                    {
                        validationResult = BuildValidationResult(stageDiagnostics, conn.GroupCount, m);
                    }

                    var pp = Path.Combine(outDir, $"{phFileIdx:D2}_{phaseName}");
                    File.WriteAllText(pp + ".json", m.ToJson(phase: "C", stageName: phaseName,
                        connectivity: conn, healthMetrics: health, stageDiagnostics: pdtos,
                        stageIndex: phFileIdx, processingDurationMs: durationMs, stageDeltas: phaseDelta,
                        validationResult: validationResult, stageHistory: isLastPhase ? stageHistory : null));
                    using (var w = new StreamWriter(pp + ".bdf")) BdfWriter.Write(m, w, pipePidBase: options.PipePidBase);

                    stageSummary.AddStage(StageSummaryJson.FromPhase(
                        phaseIndex: phFileIdx,
                        phaseName: phaseName,
                        jsonFile: Path.GetFileName(pp + ".json"),
                        bdfFile: Path.GetFileName(pp + ".bdf"),
                        processingDurationMs: durationMs,
                        model: m,
                        delta: phaseDelta,
                        connectivity: conn,
                        healthMetrics: health,
                        diagnostics: pdtos));
                    File.WriteAllText(stageSummaryFile, stageSummary.ToJson());

                    var pct = conn.LargestGroupNodeRatio * 100;
                    Console.WriteLine($"  ╔══ [{phFileIdx:D2}] Phase: {phaseName}");
                    Console.WriteLine($"  ╟── [연결성] 그룹 {conn.GroupCount}개 | 최대 {conn.LargestGroupElementCount}요소 ({pct:F1}%) | 고립노드 {conn.IsolatedNodeCount}개");
                    var iss = health.Issues;
                    Console.WriteLine($"  ╟── [건전성] 자유단 {iss.FreeEndNodes} · 고립 {iss.OrphanNodes} · 짧은 {iss.ShortElements} · 미해결U볼트 {iss.UnresolvedUbolts} · 분리그룹 {iss.DisconnectedGroups}");
                    Console.WriteLine($"  ╚══→ {pp}.json");

                    // Reset for next phase
                    prevPhaseStage  = stageName;
                    phaseStartElems = m.Elements.Count;
                    phaseStartNodes = m.Nodes.Count;
                    phElCreated = phElRemoved = phElSplit = phNdMerged = phNdMoved = 0;
                    phNdCreated = phNdRemoved = 0;
                    phaseAccumDiags.Clear();
                }
            });

        foreach (var sr in report.Stages)
            foreach (var diag in sr.Diagnostics)
                Console.WriteLine($"  [{diag.Severity}] {diag.Code}: {diag.Message}");

        bool hasError = report.Stages.SelectMany(s => s.Diagnostics)
            .Any(d => d.Severity == DiagnosticSeverity.Error);

        if (report.Succeeded)
        {
            var boundaryIds = model.Nodes
                .Where(n => n.HasTag(NodeTags.Boundary))
                .Select(n => n.Id);

            AnalysisCase? analysis = null;
            IEnumerable<int> spcNodeIds = boundaryIds;
            if (runNastran)
            {
                // RBE2 dependent 노드는 SPC1로 잡으면 GP4(FATAL 2101) 발생 → 제외 필수
                var rbeDepSet = model.Rigids
                    .SelectMany(r => r.DependentNodeIds)
                    .ToHashSet();
                var legBottomIds = ResolveLegBottomNodes(model, legZTol).ToList();
                int dropped = 0;
                var seed = boundaryIds.Concat(legBottomIds).Distinct()
                    .Where(id =>
                    {
                        if (rbeDepSet.Contains(id)) { dropped++; return false; }
                        return true;
                    }).ToList();

                // 각 connected group마다 최소 1개 SPC 보장 — 미구속 그룹은 mechanism으로
                // 작용하여 KLL singular(FATAL 9050)를 일으킴
                spcNodeIds = EnsureSpcPerGroup(model, seed, rbeDepSet, out int addedForGroups);
                analysis = new AnalysisCase();
                Console.WriteLine(
                    $"SPC1: leg-하부 {legBottomIds.Count}개 + Boundary 태그, " +
                    $"RBE2 dep 제외 {dropped}개, group 커버리지 보강 {addedForGroups}개 " +
                    $"→ 최종 {spcNodeIds.Count()}개 노드 (123456)");
            }

            // 최종 산출물 파일명 — CSV 입력 파일명에서 struData/pipeData/equpData 토큰을 제거.
            // 예) "3468-35030-struData-A453412_20260108.csv" → "3468-35030-A453412_20260108.bdf"
            string designName = DeriveDesignName(baseName, inputPaths);
            var bdfFile  = Path.Combine(outDir, $"{designName}.bdf");
            var finalJsonFile = Path.Combine(outDir, $"{designName}.json");

            // ── SPC 태그 동기화 ──
            // 최종 BDF SPC1 노드 집합을 모델에 NodeTags.Spc 로 부여하고,
            // 이미 작성된 phase/detail JSON 에도 후처리로 "Spc" 태그를 주입한다.
            // 이후 BDF/Final JSON 출력은 자동으로 Spc 태그를 반영한다.
            var spcNodeIdSet = spcNodeIds.ToHashSet();
            int spcTagged = ApplySpcTag(model, spcNodeIdSet);
            int spcJsonsPatched = PatchPhaseJsonsWithSpcTag(outDir, spcNodeIdSet);
            spcNodeIds = spcNodeIdSet; // BDF Writer 가 다시 열거하므로 List 화

            // --ubolt-full-fix: 최종 BDF/JSON 모두 cm="123456" 으로 일치시킨다 (phase JSON 은 원본 보존)
            int uboltCmReplaced = uboltFullFix ? ApplyUboltFullDof(model) : 0;

            using (var writer = new StreamWriter(bdfFile))
                BdfWriter.Write(model, writer, spcNodeIds, analysis, uboltFullFix, options.PipePidBase);

            // 최종 모델 JSON — 사용자가 다운로드받을 산출물의 phase 단계와 동일 메타로 직렬화
            var finalConn   = ConnectivityAnalyzer.Analyze(model);
            var finalDtos   = ModelDiagnosticsAnalyzer.Analyze(model, options.Tolerances);
            var finalHealth = HealthMetricsAnalyzer.Analyze(model,
                report.Stages.SelectMany(s => s.Diagnostics).ToList(), options.Tolerances);
            finalHealth.DiagnosticCounts = CountDiagnostics(finalDtos);
            var finalValidation = BuildValidationResult(
                report.Stages.SelectMany(s => s.Diagnostics).ToList(), finalConn.GroupCount, model);
            File.WriteAllText(finalJsonFile, model.ToJson(
                phase: "C",
                stageName: "Final",
                connectivity: finalConn,
                healthMetrics: finalHealth,
                stageDiagnostics: finalDtos,
                stageIndex: 99,
                processingDurationMs: 0,
                validationResult: finalValidation,
                stageHistory: stageHistory));

            // StageSummary에 모델 전체 질량/무게중심 추가 후 재저장
            var massProps = MassPropertiesAnalyzer.Analyze(model);
            stageSummary.Summary.MassProperties = new StageSummaryMassProperties
            {
                TotalMassTon = massProps.TotalMassTon,
                BeamMassTon  = massProps.BeamMassTon,
                PointMassTon = massProps.PointMassTon,
                CenterOfGravityMm = new[]
                {
                    massProps.CenterOfGravity.X,
                    massProps.CenterOfGravity.Y,
                    massProps.CenterOfGravity.Z,
                },
            };
            File.WriteAllText(stageSummaryFile, stageSummary.ToJson());

            Console.WriteLine($"BDF:  {bdfFile}");
            Console.WriteLine($"JSON: {finalJsonFile}");
            Console.WriteLine($"Mass: {massProps.TotalMassTon * 1000:F1} kg " +
                $"(beam {massProps.BeamMassTon * 1000:F1} + point {massProps.PointMassTon * 1000:F1}), " +
                $"CG=({massProps.CenterOfGravity.X:F1}, {massProps.CenterOfGravity.Y:F1}, {massProps.CenterOfGravity.Z:F1}) mm");
            if (uboltFullFix)
            {
                int uboltCount = model.Rigids.Count(r => r.Remark == "UBOLT");
                Console.WriteLine($"UBOLT cm → \"123456\" 강제 적용 (BDF/JSON 모두 반영, 변경 {uboltCmReplaced}/{uboltCount}개, --ubolt-full-fix)");
            }
            Console.WriteLine($"SPC 태그: {spcTagged}개 노드에 NodeTags.Spc 부여, phase/detail JSON {spcJsonsPatched}개 갱신");

            if (runNastran)
                RunNastran(bdfFile, nastranPath);
        }

        Console.WriteLine(report.Succeeded ? "파이프라인 완료." : "파이프라인 실패.");
        if (report.StoppedAfterStage is not null)
            Console.WriteLine($"--stopat '{report.StoppedAfterStage}' 에서 중단.");

        if (hasError)  return 2;
        if (!report.Succeeded) return 1;
        return 0;
    });

    return cmd;
}

// ── apply-edit-intent ───────────────────────────────────────────────────────

static Command BuildApplyEditIntentCommand()
{
    var folderArgument = new Argument<DirectoryInfo>("folder")
    {
        Description = "viewer가 *_edit.json을 저장한 결과 폴더",
    };
    var outOption = new Option<DirectoryInfo?>("--out")
    {
        Description = "편집 결과 출력 폴더 (기본: <folder>/edited)",
    };
    var strictOption = new Option<bool>("--strict")
    {
        Description = "viewer warning intent도 error로 처리",
    };

    var cmd = new Command("apply-edit-intent", "viewer *_edit.json을 적용해 수정 반영 BDF/JSON 생성");
    cmd.Add(folderArgument);
    cmd.Add(outOption);
    cmd.Add(strictOption);

    cmd.SetAction(parseResult =>
    {
        var folder = parseResult.GetValue(folderArgument)!;
        var outDir = parseResult.GetValue(outOption);
        bool strict = parseResult.GetValue(strictOption);
        return ApplyEditIntent(folder, outDir, strict);
    });

    return cmd;
}

static int ApplyEditIntent(DirectoryInfo folder, DirectoryInfo? outDirOption, bool strict)
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
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[오류] base JSON을 FeModel로 변환할 수 없습니다: {ex.Message}");
        return 64;
    }

    var baseConnectivity = ConnectivityAnalyzer.Analyze(model);
    var baseGroupsById = baseConnectivity.Groups.ToDictionary(g => g.Id);
    var operations = new List<Dictionary<string, object?>>();

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

        string? validationError = ValidateIntent(intent, model, baseGroupsById);
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

        foreach (var intent in doc.Intents)
        {
            if (intent.Kind == "deleteGroup")
            {
                int groupId = intent.Params.GetProperty("groupId").GetInt32();
                ApplyDeleteGroup(model, baseGroupsById[groupId], intent, operations);
            }
            else if (intent.Kind == "addRigid")
            {
                ApplyAddRigid(model, intent, operations);
            }
            else
            {
                throw new InvalidOperationException($"unsupported intent kind: {intent.Kind}");
            }
        }

        PurgeDanglingReferences(model, operations, intentId: null);

        var diagnostics = ModelDiagnosticsAnalyzer.Analyze(model, new Tolerances());
        var connectivity = ConnectivityAnalyzer.Analyze(model);
        var health = HealthMetricsAnalyzer.Analyze(model, null, new Tolerances());
        health.DiagnosticCounts = CountDiagnostics(diagnostics);
        var validation = BuildValidationResult(
            diagnostics.Select(DiagnosticFromDto).ToList(),
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

static StageMetaSnapshot ReadStageMeta(string json)
{
    using var doc = JsonDocument.Parse(json);
    var meta = doc.RootElement.GetProperty("meta");
    return new StageMetaSnapshot(
        meta.GetProperty("phase").GetString() ?? "",
        meta.GetProperty("stageName").GetString() ?? "",
        meta.TryGetProperty("stageIndex", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : 0,
        meta.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "");
}

static AnalysisCase? ReadAnalysisCaseFromBdf(string bdfPath)
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

static IReadOnlyList<int> ReadSpcNodeIdsFromBdf(string bdfPath, FeModel model)
{
    var existingNodeIds = model.Nodes.Select(n => n.Id).ToHashSet();
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

static int? ParseCaseControlInt(string line)
{
    var idx = line.IndexOf('=');
    if (idx < 0) return null;
    var value = line[(idx + 1)..].Trim();
    return int.TryParse(value, out var parsed) ? parsed : null;
}

static List<string> SplitBdfFields(string line)
{
    return line.Contains(',')
        ? line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
        : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}

static bool TryParseDouble(string value, out double result)
{
    return double.TryParse(
        value,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out result);
}

static string? ValidateIntent(
    EditIntent intent,
    FeModel model,
    IReadOnlyDictionary<int, ConnectivityGroupDto> baseGroupsById)
{
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
        if (!intent.Params.TryGetProperty("independentNode", out var indepProp) ||
            indepProp.ValueKind != JsonValueKind.Number)
            return "addRigid.params.independentNode is required.";
        if (!intent.Params.TryGetProperty("dependentNodes", out var depsProp) ||
            depsProp.ValueKind != JsonValueKind.Array)
            return "addRigid.params.dependentNodes is required.";

        int indep = indepProp.GetInt32();
        var deps = depsProp.EnumerateArray().Select(x => x.GetInt32()).Distinct().ToList();
        var nodeIds = model.Nodes.Select(n => n.Id).ToHashSet();
        if (!nodeIds.Contains(indep)) return $"independentNode {indep} not found.";
        if (deps.Count == 0) return "dependentNodes is empty.";
        foreach (int dep in deps)
            if (!nodeIds.Contains(dep)) return $"dependentNode {dep} not found.";

        string cm = "";
        if (intent.Params.TryGetProperty("cm", out var cmProp) && cmProp.ValueKind == JsonValueKind.String)
            cm = cmProp.GetString() ?? "";
        if (!string.IsNullOrWhiteSpace(cm) && !IsValidCm(cm))
            return $"invalid cm '{cm}'.";
        return null;
    }

    return $"unsupported intent kind: {intent.Kind}";
}

static void ApplyDeleteGroup(
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

static void ApplyAddRigid(
    FeModel model,
    EditIntent intent,
    List<Dictionary<string, object?>> operations)
{
    var p = intent.Params;
    int indep = p.GetProperty("independentNode").GetInt32();
    var deps = p.GetProperty("dependentNodes")
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

    foreach (var rigid in model.Rigids)
    {
        if (rigid.IndependentNodeId == indep &&
            rigid.DependentNodeIds.OrderBy(x => x).SequenceEqual(deps))
        {
            operations.Add(EditTrace.Operation(
                "DUPLICATE_RIGID_SKIPPED", "warning", intent.Id,
                details: $"existing RBE2 {rigid.Id} has same GN/GM"));
            return;
        }
    }

    int newId = NextUnusedElementId(model);
    model.Rigids.Add(new RigidElement(newId, indep, deps, remark, "viewer-added", cm));
    operations.Add(EditTrace.Operation(
        "INTENT_APPLIED", "info", intent.Id,
        kind: "addRigid",
        details: $"RBE2 {newId} created (remark={remark}, GN={indep}, GM=[{string.Join(",", deps)}], CM={cm})"));
}

static void PurgeDanglingReferences(
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

static int NextUnusedElementId(FeModel model)
{
    var used = model.Elements.Select(e => e.Id)
        .Concat(model.Rigids.Select(r => r.Id))
        .Concat(model.PointMasses.Select(pm => pm.Id))
        .ToHashSet();
    for (int id = 1; id < int.MaxValue; id++)
        if (!used.Contains(id)) return id;
    throw new InvalidOperationException("No unused element ID available.");
}

static bool IsValidCm(string cm)
{
    var seen = new HashSet<char>();
    foreach (char ch in cm)
    {
        if (ch is < '1' or > '6') return false;
        if (!seen.Add(ch)) return false;
    }
    return cm.Length is >= 1 and <= 6;
}

static Diagnostic DiagnosticFromDto(DiagnosticDto dto)
{
    var severity = dto.Severity.ToLowerInvariant() switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };
    return new Diagnostic(severity, dto.Code, dto.Message, dto.ElemId, dto.NodeId);
}

static ValidationResultDto BuildValidationResult(
    IReadOnlyList<Diagnostic> stageDiagnostics, int groupCount, FeModel m)
{
    int ec = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    int wc = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
    int zeroCount       = stageDiagnostics.Count(d => d.Code == "ZERO_LENGTH_ELEMENT");
    int shortCount      = stageDiagnostics.Count(d => d.Code == "SHORT_ELEMENT");
    int emptyRbeCount   = stageDiagnostics.Count(d => d.Code == "EMPTY_RIGID");
    int idOverflowCount = stageDiagnostics.Count(d => d.Code == "ID_OVERFLOW");
    int orphanCount     = stageDiagnostics.Count(d => d.Code == "ORPHAN_NODE");
    int freeEndCount    = stageDiagnostics.Count(d => d.Code == "FREE_END_NODE");
    bool sg = groupCount <= 1;
    bool nz = zeroCount       == 0;
    bool ns = shortCount      == 0;
    bool no = orphanCount     == 0;
    bool nr = emptyRbeCount   == 0;
    bool ie = idOverflowCount == 0;
    int bc = m.Nodes.Count(n => n.HasTag(NodeTags.Boundary));

    string summary;
    if (ec > 0)
    {
        summary = $"모델에 오류 {ec}개가 있습니다 (경고 {wc}개).";
    }
    else
    {
        var parts = new List<string>();
        if (freeEndCount > 0) parts.Add($"자유단 {freeEndCount}");
        if (orphanCount  > 0) parts.Add($"고립노드 {orphanCount}");
        if (shortCount   > 0) parts.Add($"짧은요소 {shortCount}");
        string detail = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
        summary = $"모델이 유효합니다. 경고 {wc}개{detail}.";
    }

    return new ValidationResultDto
    {
        Passed = ec == 0, ErrorCount = ec, WarningCount = wc,
        Summary = summary,
        Checklist =
        [
            new() { Check = "connectivity",        Passed = sg,     Detail = sg ? "단일 연결 그룹"   : $"{groupCount}개 그룹 분리" },
            new() { Check = "noZeroLength",        Passed = nz,     Detail = nz ? "길이=0 요소 없음" : $"{zeroCount}개 길이=0 요소 (해석 불가)" },
            new() { Check = "noShortElements",     Passed = ns,     Detail = ns ? "짧은 요소 없음"   : $"{shortCount}개 짧은 요소 (메쉬 검토 권장)" },
            new() { Check = "noOrphanNodes",       Passed = no,     Detail = no ? "고립 노드 없음"   : $"{orphanCount}개 고립 노드" },
            new() { Check = "noEmptyRigids",       Passed = nr,     Detail = nr ? "유효 RBE2"        : $"{emptyRbeCount}개 빈 dep RBE2" },
            new() { Check = "bdfExportCompatible", Passed = ie,     Detail = ie ? "ID 8자리 이내"    : $"{idOverflowCount}개 ID 한계 초과" },
            new() { Check = "boundaryCondition",   Passed = bc > 0, Detail = bc > 0 ? $"경계조건 노드 {bc}개" : "경계조건 없음" },
        ],
    };
}

static IReadOnlyList<Diagnostic> PreconnectInitialUbolts(
    FeModel model,
    RunOptions options,
    ILogger logger)
{
    logger.LogInformation("Initial UBOLT RBE preconnect starting");

    var uboltCtx = new StageContext(model, options, logger);
    new UboltRbeStage().Execute(uboltCtx);

    if (model.Rigids.Any(r => r.Remark == "UBOLT" && r.DependentNodeIds.Count > 0))
    {
        new SplitByExistingNodesStage().Execute(new StageContext(model, options, logger));
        int verticalized = UboltRbeStage.VerticalizeSafeUboltAnchors(uboltCtx);
        if (verticalized > 0)
            uboltCtx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_ANCHOR_VERTICALIZED",
                $"{verticalized} U-bolt anchor node(s) moved onto vertical support line after pipe split.");
    }

    logger.LogInformation("Initial UBOLT RBE preconnect completed");
    return uboltCtx.Diagnostics;
}

static List<int> EnsureSpcPerGroup(
    FeModel model,
    IEnumerable<int> seedSpcIds,
    HashSet<int> rbeDepSet,
    out int addedCount)
{
    var connectivity = ConnectivityAnalyzer.Analyze(model);
    var nodeById = model.Nodes.ToDictionary(n => n.Id);

    var struElemNodes = new HashSet<int>();
    var elemNodes = new HashSet<int>();
    foreach (var e in model.Elements)
    {
        elemNodes.Add(e.StartNodeId);
        elemNodes.Add(e.EndNodeId);
        if (e.Category == EntityCategory.Structure)
        {
            struElemNodes.Add(e.StartNodeId);
            struElemNodes.Add(e.EndNodeId);
        }
    }

    var spcSet = seedSpcIds.ToHashSet();
    addedCount = 0;
    foreach (var group in connectivity.Groups)
    {
        if (group.NodeIds.Any(spcSet.Contains)) continue;

        // 우선순위: Structure 노드 minZ → 임의 element-연결 노드 minZ → 그룹 내 minZ
        Node? pick = group.NodeIds
            .Where(id => !rbeDepSet.Contains(id) && struElemNodes.Contains(id) && nodeById.ContainsKey(id))
            .Select(id => nodeById[id])
            .OrderBy(n => n.Position.Z).FirstOrDefault()
            ?? group.NodeIds
                .Where(id => !rbeDepSet.Contains(id) && elemNodes.Contains(id) && nodeById.ContainsKey(id))
                .Select(id => nodeById[id])
                .OrderBy(n => n.Position.Z).FirstOrDefault()
            ?? group.NodeIds
                .Where(id => !rbeDepSet.Contains(id) && nodeById.ContainsKey(id))
                .Select(id => nodeById[id])
                .OrderBy(n => n.Position.Z).FirstOrDefault();

        if (pick is not null)
        {
            spcSet.Add(pick.Id);
            addedCount++;
        }
    }
    return spcSet.ToList();
}

static IEnumerable<int> ResolveLegBottomNodes(FeModel model, double tolMm)
{
    var struElemNodes = new HashSet<int>();
    foreach (var e in model.Elements)
    {
        if (e.Category != EntityCategory.Structure) continue;
        struElemNodes.Add(e.StartNodeId);
        struElemNodes.Add(e.EndNodeId);
    }
    if (struElemNodes.Count == 0) return [];

    var struNodes = model.Nodes.Where(n => struElemNodes.Contains(n.Id)).ToList();
    if (struNodes.Count == 0) return [];

    double minZ = struNodes.Min(n => n.Position.Z);
    return struNodes
        .Where(n => n.Position.Z <= minZ + tolMm)
        .Select(n => n.Id)
        .ToList();
}

static void RunNastran(string bdfPath, string nastranPath)
{
    if (!File.Exists(nastranPath))
    {
        Console.Error.WriteLine($"[Nastran] 실행 파일 없음: {nastranPath}");
        return;
    }

    var workDir = Path.GetDirectoryName(bdfPath)!;
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = nastranPath,
        Arguments = $"\"{bdfPath}\" scr=yes batch=no old=no",
        WorkingDirectory = workDir,
        UseShellExecute = false,
        CreateNoWindow = false,
    };

    Console.WriteLine($"[Nastran] 실행: {nastranPath} {psi.Arguments}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var proc = System.Diagnostics.Process.Start(psi);
    if (proc is null)
    {
        Console.Error.WriteLine("[Nastran] 프로세스 시작 실패");
        return;
    }
    proc.WaitForExit();
    sw.Stop();
    Console.WriteLine($"[Nastran] 종료 코드 {proc.ExitCode} ({sw.Elapsed.TotalSeconds:F1}s)");

    var f06 = Path.ChangeExtension(bdfPath, ".f06");
    if (File.Exists(f06))
    {
        Console.WriteLine($"[Nastran] F06: {f06}");
        ReportFatalsFromF06(f06);
    }
    else
    {
        Console.Error.WriteLine($"[Nastran] F06 파일 없음: {f06}");
    }
}

static void ReportFatalsFromF06(string f06Path)
{
    int fatalCount = 0, warningCount = 0;
    var fatals = new List<string>();
    foreach (var line in File.ReadLines(f06Path))
    {
        if (line.Contains("*** USER FATAL", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("*** SYSTEM FATAL", StringComparison.OrdinalIgnoreCase))
        {
            fatalCount++;
            if (fatals.Count < 10) fatals.Add(line.TrimEnd());
        }
        else if (line.Contains("*** USER WARNING", StringComparison.OrdinalIgnoreCase))
        {
            warningCount++;
        }
    }

    Console.WriteLine($"[Nastran] FATAL {fatalCount}건, WARNING {warningCount}건");
    foreach (var f in fatals)
        Console.WriteLine($"  {f}");
    if (fatalCount > fatals.Count)
        Console.WriteLine($"  ... (외 {fatalCount - fatals.Count}건)");
}

static DiagnosticCountsDto CountDiagnostics(IReadOnlyList<DiagnosticDto> diagnostics)
{
    var counts = new DiagnosticCountsDto();
    foreach (var diagnostic in diagnostics)
    {
        switch (diagnostic.Severity.ToLowerInvariant())
        {
            case "error": counts.Error++; break;
            case "warning": counts.Warning++; break;
            case "info": counts.Info++; break;
        }
        counts.ByCode[diagnostic.Code] = counts.ByCode.GetValueOrDefault(diagnostic.Code) + 1;
    }
    return counts;
}

internal sealed class StageSummaryDocument
{
    [JsonPropertyName("_keys")] public Dictionary<string, string> Keys { get; set; } = StageSummaryJson.SchemaKeys;
    [JsonPropertyName("meta")] public StageSummaryMeta Meta { get; set; } = new();
    [JsonPropertyName("summary")] public StageSummaryTotals Summary { get; set; } = new();
    [JsonPropertyName("stages")] public List<StageSummaryRow> Stages { get; set; } = [];

    public void AddStage(StageSummaryRow row)
    {
        Stages.RemoveAll(s => s.StageIndex == row.StageIndex);
        Stages.Add(row);
        Stages.Sort((a, b) => a.StageIndex.CompareTo(b.StageIndex));
        StageSummaryJson.PopulateSummary(this);
    }

    public string ToJson() => JsonSerializer.Serialize(this, StageSummaryJson.Options);
}

internal sealed class StageSummaryMeta
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } =
        "Phase-level algorithm summary index. Detailed FEM state remains in each phase JSON.";
}

internal sealed class StageSummaryTotals
{
    [JsonPropertyName("stageCount")] public int StageCount { get; set; }
    [JsonPropertyName("firstStage")] public string FirstStage { get; set; } = "";
    [JsonPropertyName("lastStage")] public string LastStage { get; set; } = "";
    [JsonPropertyName("finalNodeCount")] public int FinalNodeCount { get; set; }
    [JsonPropertyName("finalElementCount")] public int FinalElementCount { get; set; }
    [JsonPropertyName("finalRigidCount")] public int FinalRigidCount { get; set; }
    [JsonPropertyName("finalPointMassCount")] public int FinalPointMassCount { get; set; }
    [JsonPropertyName("totalErrors")] public int TotalErrors { get; set; }
    [JsonPropertyName("totalWarnings")] public int TotalWarnings { get; set; }
    [JsonPropertyName("totalInfos")] public int TotalInfos { get; set; }

    [JsonPropertyName("massProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StageSummaryMassProperties? MassProperties { get; set; }
}

internal sealed class StageSummaryMassProperties
{
    [JsonPropertyName("totalMassTon")] public double TotalMassTon { get; set; }
    [JsonPropertyName("beamMassTon")] public double BeamMassTon { get; set; }
    [JsonPropertyName("pointMassTon")] public double PointMassTon { get; set; }
    [JsonPropertyName("centerOfGravityMm")] public double[] CenterOfGravityMm { get; set; } = [0, 0, 0];
}

internal sealed class StageSummaryRow
{
    [JsonPropertyName("stageIndex")] public int StageIndex { get; set; }
    [JsonPropertyName("stageName")] public string StageName { get; set; } = "";
    [JsonPropertyName("jsonFile")] public string JsonFile { get; set; } = "";
    [JsonPropertyName("bdfFile")] public string BdfFile { get; set; } = "";
    [JsonPropertyName("processingDurationMs")] public long ProcessingDurationMs { get; set; }
    [JsonPropertyName("counts")] public StageSummaryCounts Counts { get; set; } = new();
    [JsonPropertyName("delta")] public StageSummaryDelta Delta { get; set; } = new();
    [JsonPropertyName("connectivity")] public StageSummaryConnectivity Connectivity { get; set; } = new();
    [JsonPropertyName("health")] public StageSummaryHealth Health { get; set; } = new();
    [JsonPropertyName("diagnostics")] public StageSummaryDiagnostics Diagnostics { get; set; } = new();
}

internal sealed class StageSummaryCounts
{
    [JsonPropertyName("nodes")] public int Nodes { get; set; }
    [JsonPropertyName("elements")] public int Elements { get; set; }
    [JsonPropertyName("rigids")] public int Rigids { get; set; }
    [JsonPropertyName("pointMasses")] public int PointMasses { get; set; }
}

internal sealed class StageSummaryDelta
{
    [JsonPropertyName("elementsCreated")] public int ElementsCreated { get; set; }
    [JsonPropertyName("elementsRemoved")] public int ElementsRemoved { get; set; }
    [JsonPropertyName("elementsSplit")] public int ElementsSplit { get; set; }
    [JsonPropertyName("nodesMerged")] public int NodesMerged { get; set; }
    [JsonPropertyName("nodesMoved")] public int NodesMoved { get; set; }
    [JsonPropertyName("netElementDelta")] public int NetElementDelta { get; set; }
    [JsonPropertyName("netNodeDelta")] public int NetNodeDelta { get; set; }
}

internal sealed class StageSummaryConnectivity
{
    [JsonPropertyName("groupCount")] public int GroupCount { get; set; }
    [JsonPropertyName("largestGroupElementCount")] public int LargestGroupElementCount { get; set; }
    [JsonPropertyName("largestGroupNodeRatio")] public double LargestGroupNodeRatio { get; set; }
    [JsonPropertyName("isolatedNodeCount")] public int IsolatedNodeCount { get; set; }
}

internal sealed class StageSummaryHealth
{
    [JsonPropertyName("freeEndCount")] public int FreeEndCount { get; set; }
    [JsonPropertyName("orphanNodeCount")] public int OrphanNodeCount { get; set; }
    [JsonPropertyName("shortElementCount")] public int ShortElementCount { get; set; }
    [JsonPropertyName("unresolvedUboltCount")] public int UnresolvedUboltCount { get; set; }
    [JsonPropertyName("disconnectedGroupCount")] public int DisconnectedGroupCount { get; set; }
}

internal sealed class StageSummaryDiagnostics
{
    [JsonPropertyName("error")] public int Error { get; set; }
    [JsonPropertyName("warning")] public int Warning { get; set; }
    [JsonPropertyName("info")] public int Info { get; set; }
}

internal static class StageSummaryJson
{
    internal static readonly Dictionary<string, string> SchemaKeys = new()
    {
        ["meta"] = "단계 요약 메타 정보 (schemaVersion, timestamp, unit, purpose)",
        ["summary"] = "전체 phase 실행 요약 (stageCount, firstStage, lastStage, final counts, diagnostic totals, massProperties)",
        ["summary.massProperties"] = "최종 모델 전체 질량/무게중심 — totalMassTon(beam+point 합), beamMassTon, pointMassTon, centerOfGravityMm[x,y,z]",
        ["stages"] = "phase별 FEM 상태 및 변화량 요약 배열. 상세 노드/요소 목록은 각 phase JSON에서 확인",
        ["stages.stageIndex"] = "phase 출력 번호. 파일명 접두 번호와 동일",
        ["stages.stageName"] = "phase 이름",
        ["stages.jsonFile"] = "해당 phase 상세 JSON 파일명",
        ["stages.bdfFile"] = "해당 phase BDF 파일명",
        ["stages.processingDurationMs"] = "phase boundary를 생성한 마지막 내부 stage 처리 시간(ms)",
        ["stages.counts"] = "phase 완료 시점의 nodes/elements/rigids/pointMasses 개수",
        ["stages.delta"] = "이전 phase 대비 created/removed/split/merged/moved 및 net 변화량",
        ["stages.connectivity"] = "phase 완료 시점 연결성 요약",
        ["stages.health"] = "phase 완료 시점 주요 건전성 지표",
        ["stages.diagnostics"] = "phase JSON에 포함된 진단 severity별 개수",
    };

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static StageSummaryRow FromPhase(
        int phaseIndex,
        string phaseName,
        string jsonFile,
        string bdfFile,
        long processingDurationMs,
        FeModel model,
        StageDeltasDto delta,
        ConnectivityDto connectivity,
        HealthMetricsDto healthMetrics,
        IReadOnlyList<DiagnosticDto> diagnostics)
    {
        var counts = CountStageDiagnostics(diagnostics);
        return new StageSummaryRow
        {
            StageIndex = phaseIndex,
            StageName = phaseName,
            JsonFile = jsonFile,
            BdfFile = bdfFile,
            ProcessingDurationMs = processingDurationMs,
            Counts = new StageSummaryCounts
            {
                Nodes = model.Nodes.Count,
                Elements = model.Elements.Count,
                Rigids = model.Rigids.Count,
                PointMasses = model.PointMasses.Count,
            },
            Delta = new StageSummaryDelta
            {
                ElementsCreated = delta.ElementsCreated,
                ElementsRemoved = delta.ElementsRemoved,
                ElementsSplit = delta.ElementsSplit,
                NodesMerged = delta.NodesMerged,
                NodesMoved = delta.NodesMoved,
                NetElementDelta = delta.NetElementDelta,
                NetNodeDelta = delta.NetNodeDelta,
            },
            Connectivity = new StageSummaryConnectivity
            {
                GroupCount = connectivity.GroupCount,
                LargestGroupElementCount = connectivity.LargestGroupElementCount,
                LargestGroupNodeRatio = connectivity.LargestGroupNodeRatio,
                IsolatedNodeCount = connectivity.IsolatedNodeCount,
            },
            Health = new StageSummaryHealth
            {
                FreeEndCount = healthMetrics.Issues.FreeEndNodes,
                OrphanNodeCount = healthMetrics.Issues.OrphanNodes,
                ShortElementCount = healthMetrics.Issues.ShortElements,
                UnresolvedUboltCount = healthMetrics.Issues.UnresolvedUbolts,
                DisconnectedGroupCount = healthMetrics.Issues.DisconnectedGroups,
            },
            Diagnostics = new StageSummaryDiagnostics
            {
                Error = counts.Error,
                Warning = counts.Warning,
                Info = counts.Info,
            },
        };
    }

    public static void PopulateSummary(StageSummaryDocument doc)
    {
        doc.Summary.StageCount = doc.Stages.Count;
        doc.Summary.FirstStage = doc.Stages.FirstOrDefault()?.StageName ?? "";
        doc.Summary.LastStage = doc.Stages.LastOrDefault()?.StageName ?? "";

        var final = doc.Stages.LastOrDefault();
        doc.Summary.FinalNodeCount = final?.Counts.Nodes ?? 0;
        doc.Summary.FinalElementCount = final?.Counts.Elements ?? 0;
        doc.Summary.FinalRigidCount = final?.Counts.Rigids ?? 0;
        doc.Summary.FinalPointMassCount = final?.Counts.PointMasses ?? 0;

        // Pipeline success/failure should reflect the final exported model.
        // Earlier phase errors can be transient health findings that later stages repair.
        doc.Summary.TotalErrors = final?.Diagnostics.Error ?? 0;
        doc.Summary.TotalWarnings = doc.Stages.Sum(s => s.Diagnostics.Warning);
        doc.Summary.TotalInfos = doc.Stages.Sum(s => s.Diagnostics.Info);
    }

    private static StageSummaryDiagnostics CountStageDiagnostics(IReadOnlyList<DiagnosticDto> diagnostics)
    {
        var counts = new StageSummaryDiagnostics();
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity.ToLowerInvariant())
            {
                case "error": counts.Error++; break;
                case "warning": counts.Warning++; break;
                case "info": counts.Info++; break;
            }
        }
        return counts;
    }
}

internal sealed record CsvInputPaths(string? StructurePath, string? PipePath, string? EquipmentPath);

internal sealed record StageMetaSnapshot(
    string Phase,
    string StageName,
    int StageIndex,
    string Timestamp);

internal sealed class EditIntentDocument
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("stageRef")] public EditStageRef StageRef { get; set; } = new();
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("createdBy")] public string CreatedBy { get; set; } = "";
    [JsonPropertyName("intents")] public List<EditIntent> Intents { get; set; } = [];
}

internal sealed class EditStageRef
{
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("stageName")] public string StageName { get; set; } = "";
    [JsonPropertyName("sourceTimestamp")] public string SourceTimestamp { get; set; } = "";
}

internal sealed class EditIntent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement Params { get; set; }
    [JsonPropertyName("validation")] public EditIntentValidation Validation { get; set; } = new();
}

internal sealed class EditIntentValidation
{
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
}

internal sealed class EditApplyTraceDocument
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("intentFile")] public string IntentFile { get; set; } = "";
    [JsonPropertyName("baseStage")] public string BaseStage { get; set; } = "";
    [JsonPropertyName("intents")] public List<EditIntent> Intents { get; set; } = [];
    [JsonPropertyName("operations")] public List<Dictionary<string, object?>> Operations { get; set; } = [];
}

internal static class EditTrace
{
    public static Dictionary<string, object?> Operation(
        string code,
        string level,
        string? intentId,
        string? kind = null,
        string? details = null,
        int? rbeId = null,
        string? reason = null,
        IReadOnlyList<int>? removedGM = null,
        int? massId = null,
        int? nodeId = null,
        string? card = null,
        int? removedGID = null)
    {
        var op = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["level"] = level,
        };
        if (!string.IsNullOrWhiteSpace(intentId)) op["intentId"] = intentId;
        if (!string.IsNullOrWhiteSpace(kind)) op["kind"] = kind;
        if (!string.IsNullOrWhiteSpace(details)) op["details"] = details;
        if (rbeId.HasValue) op["rbeId"] = rbeId.Value;
        if (!string.IsNullOrWhiteSpace(reason)) op["reason"] = reason;
        if (removedGM is { Count: > 0 }) op["removedGM"] = removedGM;
        if (massId.HasValue) op["massId"] = massId.Value;
        if (nodeId.HasValue) op["nodeId"] = nodeId.Value;
        if (!string.IsNullOrWhiteSpace(card)) op["card"] = card;
        if (removedGID.HasValue) op["removedGID"] = removedGID.Value;
        return op;
    }
}

internal static class EditIntentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

internal sealed class InputAuditDocument
{
    [JsonPropertyName("_keys")] public Dictionary<string, string> Keys { get; set; } = InputAuditJson.SchemaKeys;
    [JsonPropertyName("meta")] public InputAuditMeta Meta { get; set; } = new();
    [JsonPropertyName("inputFiles")] public List<InputAuditFile> InputFiles { get; set; } = [];
    [JsonPropertyName("summary")] public InputAuditSummary Summary { get; set; } = new();
    [JsonPropertyName("rowAudit")] public List<InputAuditRow> RowAudit { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, InputAuditJson.Options);
}

internal sealed class InputAuditMeta
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } =
        "Complete CSV input audit: every physical CSV data row is listed with parse/conversion status and reason.";
}

internal sealed class InputAuditFile
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("header")] public List<string> Header { get; set; } = [];
    [JsonPropertyName("physicalLineCount")] public int PhysicalLineCount { get; set; }
    [JsonPropertyName("dataRowCount")] public int DataRowCount { get; set; }
    [JsonPropertyName("blankDataRowCount")] public int BlankDataRowCount { get; set; }
}

internal sealed class InputAuditSummary
{
    [JsonPropertyName("totalDataRows")] public int TotalDataRows { get; set; }
    [JsonPropertyName("convertedRows")] public int ConvertedRows { get; set; }
    [JsonPropertyName("ignoredRows")] public int IgnoredRows { get; set; }
    [JsonPropertyName("errorRows")] public int ErrorRows { get; set; }
    [JsonPropertyName("parseFailedRows")] public int ParseFailedRows { get; set; }
    [JsonPropertyName("blankRows")] public int BlankRows { get; set; }
    [JsonPropertyName("ambiguousDuplicateSourceNameRows")] public int AmbiguousDuplicateSourceNameRows { get; set; }
    [JsonPropertyName("ignoredByReason")] public Dictionary<string, int> IgnoredByReason { get; set; } = [];
}

internal sealed class InputAuditRow
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("physicalLineNumber")] public int PhysicalLineNumber { get; set; }
    [JsonPropertyName("dataRowNumber")] public int DataRowNumber { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("mappingConfidence")] public string MappingConfidence { get; set; } = "sourceName";
    [JsonPropertyName("rawLine")] public string RawLine { get; set; } = "";
    [JsonPropertyName("rawFields")] public Dictionary<string, string> RawFields { get; set; } = [];
}

internal static class InputAuditJson
{
    internal static readonly Dictionary<string, string> SchemaKeys = new()
    {
        ["meta"] = "입력 감사 메타 정보 (schemaVersion, timestamp, unit, purpose)",
        ["inputFiles"] = "입력 CSV 파일 목록과 파일별 행 수/header 정보 (kind, path, exists, header, physicalLineCount, dataRowCount, blankDataRowCount)",
        ["summary"] = "전체 CSV 행 처리 요약 (totalDataRows, convertedRows, ignoredRows, errorRows, parseFailedRows, blankRows, ambiguousDuplicateSourceNameRows, ignoredByReason)",
        ["rowAudit"] = "모든 CSV 데이터 행 감사 배열. CSV 비헤더 행마다 하나씩 기록되며 rawLine/rawFields/status/reason을 포함",
        ["rowAudit.kind"] = "CSV 종류: Structure, Pipe, Equipment",
        ["rowAudit.file"] = "해당 행이 나온 원본 CSV 파일 경로",
        ["rowAudit.physicalLineNumber"] = "CSV 파일 내 실제 줄 번호. 헤더는 1번, 첫 데이터 행은 2번",
        ["rowAudit.dataRowNumber"] = "헤더를 제외한 데이터 행 번호. 첫 데이터 행은 1번",
        ["rowAudit.name"] = "CSV name 컬럼 값",
        ["rowAudit.status"] = "행 처리 상태: converted(CSV 변환 대상으로 수용됨), ignored(의도적으로 제외됨), error(입력 데이터 오류 — 파싱은 됐으나 변환 시 형상 누락 등 문제 발생, 예: 배관 outDia<=0), parseFailed(파싱 실패), blank(공백 행)",
        ["rowAudit.reasonCode"] = "처리 상태를 기계적으로 분류하기 위한 사유 코드",
        ["rowAudit.reason"] = "사용자가 읽을 수 있는 처리/제외/실패 사유 설명",
        ["rowAudit.mappingConfidence"] = "CSV name 식별 신뢰도. 중복 name이면 ambiguousDuplicateSourceName",
        ["rowAudit.rawLine"] = "원본 CSV 데이터 행 문자열",
        ["rowAudit.rawFields"] = "원본 CSV 컬럼명-값 사전. CSV 내용 누락 방지를 위한 원문 필드 기록",
    };

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static InputAuditDocument Build(CsvInputPaths paths, RawDesignData data)
    {
        var duplicateNames = ReadDuplicateSourceNames(paths);
        var skips = data.Skips
            .GroupBy(s => (s.Kind, s.LineNumber, s.Name))
            .ToDictionary(g => g.Key, g => g.First());

        var doc = new InputAuditDocument();
        AddFileRows(doc, "Structure", paths.StructurePath, skips, duplicateNames);
        AddFileRows(doc, "Pipe", paths.PipePath, skips, duplicateNames);
        AddFileRows(doc, "Equipment", paths.EquipmentPath, skips, duplicateNames);
        PopulateSummary(doc);
        return doc;
    }

    private static HashSet<(string Kind, string Name)> ReadDuplicateSourceNames(CsvInputPaths paths)
    {
        var result = new HashSet<(string, string)>();
        foreach (var (kind, path) in EnumeratePaths(paths))
        {
            if (path is null || !File.Exists(path)) continue;
            var names = ReadDataRows(path).Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var name in names)
                result.Add((kind, name));
        }
        return result;
    }

    private static void AddFileRows(
        InputAuditDocument doc,
        string kind,
        string? path,
        Dictionary<(string Kind, int LineNumber, string Name), ParseSkip> skips,
        HashSet<(string Kind, string Name)> duplicateNames)
    {
        if (path is null || !File.Exists(path))
        {
            doc.InputFiles.Add(new InputAuditFile { Kind = kind, Path = path ?? "", Exists = false });
            return;
        }

        var data = ReadDataRows(path);
        doc.InputFiles.Add(new InputAuditFile
        {
            Kind = kind,
            Path = path,
            Exists = true,
            Header = data.Header,
            PhysicalLineCount = data.PhysicalLineCount,
            DataRowCount = data.Rows.Count,
            BlankDataRowCount = data.Rows.Count(r => string.IsNullOrWhiteSpace(r.RawLine)),
        });

        foreach (var row in data.Rows)
        {
            skips.TryGetValue((kind, row.DataRowNumber, row.Name), out var skip);
            var (status, reasonCode, reason) = Classify(kind, row, skip);

            doc.RowAudit.Add(new InputAuditRow
            {
                Kind = kind,
                File = path,
                PhysicalLineNumber = row.PhysicalLineNumber,
                DataRowNumber = row.DataRowNumber,
                Name = row.Name,
                Status = status,
                ReasonCode = reasonCode,
                Reason = reason,
                MappingConfidence = duplicateNames.Contains((kind, row.Name))
                    ? "ambiguousDuplicateSourceName"
                    : "sourceName",
                RawLine = row.RawLine,
                RawFields = row.Fields,
            });
        }
    }

    private static (string Status, string ReasonCode, string Reason) Classify(
        string kind,
        CsvAuditRawRow row,
        ParseSkip? skip)
    {
        if (string.IsNullOrWhiteSpace(row.RawLine))
            return ("blank", "blank_line", "Blank CSV data line; parser skips blank lines.");

        if (skip is not null)
            return ("parseFailed", "parse_failed", skip.Reason);

        return kind switch
        {
            "Structure" => ClassifyIgnoredStructure(row),
            "Pipe" => ClassifyIgnoredPipe(row),
            "Equipment" => ClassifyIgnoredEquipment(row),
            _ => ("converted", "csv_row_accepted", "CSV row parsed successfully and was accepted for conversion."),
        };
    }

    private static (string, string, string) ClassifyIgnoredStructure(CsvAuditRawRow row)
    {
        var start = ParseDoubles(Field(row, "poss"));
        var end = ParseDoubles(Field(row, "pose"));
        if (start.Length >= 3 && end.Length >= 3 && SamePoint(start, end))
            return ("ignored", "zero_length_structure", "Structure start and end coordinates are identical, so it has no convertible length.");

        var size = Field(row, "size");
        var section = size.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!IsSupportedStructureSection(section))
            return ("ignored", "unsupported_structure_section", $"Structure section '{section}' is not mapped to a supported beam section kind.");

        return ("converted", "csv_row_accepted", "Structure row parsed successfully and was accepted for conversion.");
    }

    // 직선·곡선 배관 부재 타입 — outDia 로 빔 단면(PBEAML Tube)을 만들어야 하는 행.
    // FeModelBuilder.IsLinePipeType 과 동일 집합. 이들에 outDia<=0 이면 형상을 만들 수 없어
    // 모델 변환 단계에서 통째로 누락되므로 입력 데이터 오류로 본다.
    private static readonly HashSet<string> LinePipeGeometryTypes = new(StringComparer.Ordinal)
    {
        "TUBI", "ELBO", "BEND", "OLET", "REDU", "COUP", "TEE",
    };

    private static (string, string, string) ClassifyIgnoredPipe(CsvAuditRawRow row)
    {
        var type = Field(row, "type").Trim().ToUpperInvariant();
        var mass = ParseDouble(Field(row, "mass"));
        var outDia = ParseDouble(Field(row, "outDia"));
        var aPos = ParseDoubles(Field(row, "apos"));
        var lPos = ParseDoubles(Field(row, "lpos"));

        if (type == "ATTA" && mass <= 0)
            return ("ignored", "zero_mass_attachment", "ATTA row has mass <= 0, so it is excluded from CSV conversion output.");

        // 데이터 오류: 배관 부재(TUBI/ELBO/BEND/…)인데 외경(outDia)이 0 이하.
        // 빔 단면을 만들 수 없어 모델 변환 시 이 행의 형상이 그대로 누락된다(조용히 드롭됨).
        // (실제 사례: ELBO 행의 직경이 형제 TUBI 행에만 기록되어 ELBO outDia 가 0 → 곡선부 누락)
        if (LinePipeGeometryTypes.Contains(type) && outDia <= 0)
            return ("error", "pipe_outdia_zero",
                $"Pipe '{type}' row has outDia <= 0; its beam section cannot be built so the geometry is dropped during model conversion. Provide a positive outDia (for elbows the diameter is often only on the sibling TUBI row).");

        if (outDia <= 0 && mass <= 0)
            return ("ignored", "no_geometry_and_zero_mass", "Pipe row has outDia <= 0 and mass <= 0, so it has neither geometry nor mass to convert.");

        if (aPos.Length >= 3 && lPos.Length >= 3 && SamePoint(aPos, lPos) && mass <= 0)
            return ("ignored", "zero_length_pipe_without_mass", "Pipe endpoints are identical and mass <= 0, so it has no convertible geometry or mass.");

        return ("converted", "csv_row_accepted", "Pipe row parsed successfully and was accepted for conversion.");
    }

    private static (string, string, string) ClassifyIgnoredEquipment(CsvAuditRawRow row)
    {
        var mass = ParseDouble(Field(row, "mass"));
        if (mass <= 0)
            return ("ignored", "zero_mass_equipment", "Equipment mass <= 0, so it is excluded from CSV conversion output.");
        return ("converted", "csv_row_accepted", "Equipment row parsed successfully and was accepted for conversion.");
    }

    private static void PopulateSummary(InputAuditDocument doc)
    {
        doc.Summary.TotalDataRows = doc.RowAudit.Count;
        doc.Summary.ConvertedRows = doc.RowAudit.Count(r => r.Status == "converted");
        doc.Summary.IgnoredRows = doc.RowAudit.Count(r => r.Status == "ignored");
        doc.Summary.ErrorRows = doc.RowAudit.Count(r => r.Status == "error");
        doc.Summary.ParseFailedRows = doc.RowAudit.Count(r => r.Status == "parseFailed");
        doc.Summary.BlankRows = doc.RowAudit.Count(r => r.Status == "blank");
        doc.Summary.AmbiguousDuplicateSourceNameRows = doc.RowAudit.Count(r => r.MappingConfidence == "ambiguousDuplicateSourceName");
        // 문제 행(오류/제외/실패/공백)을 사유 코드별로 집계 — UI 의 사유 breakdown 에 사용.
        doc.Summary.IgnoredByReason = doc.RowAudit
            .Where(r => r.Status is "error" or "ignored" or "blank" or "parseFailed")
            .GroupBy(r => r.ReasonCode)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static (List<string> Header, List<CsvAuditRawRow> Rows, int PhysicalLineCount) ReadDataRows(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines.Length > 0
            ? SplitCsvLine(lines[0]).Select(h => h.Trim()).ToList()
            : [];
        var rows = new List<CsvAuditRawRow>();
        int dataRow = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            dataRow++;
            var raw = lines[i];
            var values = SplitCsvLine(raw);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < header.Count; c++)
                fields[header[c]] = c < values.Length ? values[c].Trim() : "";

            rows.Add(new CsvAuditRawRow(
                PhysicalLineNumber: i + 1,
                DataRowNumber: dataRow,
                Name: fields.GetValueOrDefault("name", ""),
                RawLine: raw,
                Fields: fields));
        }
        return (header, rows, lines.Length);
    }

    private static IEnumerable<(string Kind, string? Path)> EnumeratePaths(CsvInputPaths paths)
    {
        yield return ("Structure", paths.StructurePath);
        yield return ("Pipe", paths.PipePath);
        yield return ("Equipment", paths.EquipmentPath);
    }

    private static string[] SplitCsvLine(string line) => line.Split(',');

    private static string Field(CsvAuditRawRow row, string name) =>
        row.Fields.GetValueOrDefault(name, "");

    private static bool IsSupportedStructureSection(string section) =>
        section.ToUpperInvariant() is "ANG" or "BEAM" or "BSC" or "BULB" or "FBAR" or "RBAR" or "TUBE";

    private static bool SamePoint(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) < 1e-9 &&
        Math.Abs(a[1] - b[1]) < 1e-9 &&
        Math.Abs(a[2] - b[2]) < 1e-9;

    private static double[] ParseDoubles(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?");
        return matches.Select(m => ParseDouble(m.Value)).ToArray();
    }

    private static double ParseDouble(string text) =>
        double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0.0;
}

internal sealed record CsvAuditRawRow(
    int PhysicalLineNumber,
    int DataRowNumber,
    string Name,
    string RawLine,
    Dictionary<string, string> Fields);
