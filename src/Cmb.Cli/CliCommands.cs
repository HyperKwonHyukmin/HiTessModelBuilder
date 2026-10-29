// M-1 리팩터: Program.cs 에서 분리한 System.CommandLine 명령 빌더(parse/build-raw/build-full/apply-edit-intent). 동작 불변.
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

internal static class CliCommands
{
    // ── 공통 옵션 팩토리 ─────────────────────────────────────────────────────────
    
    internal static (Option<DirectoryInfo?> input, Option<FileInfo?> stru, Option<FileInfo?> pipe, Option<FileInfo?> equip)
        MakeCsvOptions()
    {
        return (
            new Option<DirectoryInfo?>("--input")  { Description = "CSV 폴더 (struData/pipeData/equpData 자동 탐색)" },
            new Option<FileInfo?>("--stru")         { Description = "Structure CSV 파일 경로" },
            new Option<FileInfo?>("--pipe")         { Description = "Pipe CSV 파일 경로" },
            new Option<FileInfo?>("--equip")        { Description = "Equipment CSV 파일 경로" }
        );
    }
    
    // ── parse ────────────────────────────────────────────────────────────────────
    
    internal static Command BuildParseCommand()
    {
        var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();
    
        var cmd = new Command("parse", "CSV를 파싱하여 raw JSON 덤프 생성");
        cmd.Add(inputOption);
        cmd.Add(struOption);
        cmd.Add(pipeOption);
        cmd.Add(equipOption);
    
        cmd.SetAction(parseResult =>
        {
            var (data, baseName, csvDir, _, error) = CsvInput.LoadCsv(
                parseResult.GetValue(inputOption),
                parseResult.GetValue(struOption),
                parseResult.GetValue(pipeOption),
                parseResult.GetValue(equipOption));
    
            if (error is not null) { Console.Error.WriteLine(error); return 1; }
    
            var outDir  = CsvInput.MakeOutputDir(csvDir);
    
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
    
    internal static Command BuildBuildRawCommand()
    {
        var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();
    
        var cmd = new Command("build-raw", "CSV → FeModel 변환 (알고리즘 미적용 원본 모델)");
        cmd.Add(inputOption);
        cmd.Add(struOption);
        cmd.Add(pipeOption);
        cmd.Add(equipOption);
    
        cmd.SetAction(parseResult =>
        {
            var (data, baseName, csvDir, _, error) = CsvInput.LoadCsv(
                parseResult.GetValue(inputOption),
                parseResult.GetValue(struOption),
                parseResult.GetValue(pipeOption),
                parseResult.GetValue(equipOption));
    
            if (error is not null) { Console.Error.WriteLine(error); return 1; }
    
            var outDir = CsvInput.MakeOutputDir(csvDir);
    
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
    
    internal static Command BuildBuildFullCommand()
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
            Description = "최종 BDF에 자동 생성된 SPC1/GRAV 하중 조건으로 Nastran 실행",
        };
        var nastranPathOption = new Option<string>("--nastran-path")
        {
            Description = "Nastran 실행 파일 경로 (기본: C:\\MSC.Software\\MSC_Nastran\\20131\\bin\\nastran.exe)",
            DefaultValueFactory = _ => @"C:\MSC.Software\MSC_Nastran\20131\bin\nastran.exe",
        };
        var legZTolOption = new Option<double>("--leg-z-tol")
        {
            Description = "모델 최저 Z 기준 SPC 검출 허용 Z 범위 (mm, 기본 500)",
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
    
        var hullNoOption = new Option<string?>("--hull-no")
        {
            Description = "호선 번호 (예: 3454). 최종·phase BDF 첫 줄 '$ HiTessModelBuilder: HullNo=...' 주석과 JSON meta.hullNo 로 남긴다",
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
        cmd.Add(hullNoOption);
    
        cmd.SetAction(parseResult =>
        {
            try
            {
            var (data, baseName, csvDir, inputPaths, error) = CsvInput.LoadCsv(
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
            var outDir       = CsvInput.MakeOutputDir(csvDir);
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
            // 호선 — 형식이 맞지 않으면 버리고 경고만 남긴다(호선 때문에 모델 생성을 막지 않는다).
            var hullNoRaw = parseResult.GetValue(hullNoOption);
            model.HullNo = FeModel.NormalizeHullNo(hullNoRaw);
            if (model.HullNo is not null)
                Console.WriteLine($"호선: {model.HullNo}");
            else if (!string.IsNullOrWhiteSpace(hullNoRaw))
                Console.Error.WriteLine($"[경고] 호선 '{hullNoRaw}' 형식이 맞지 않아 BDF 에 남기지 않습니다 (영문·숫자·'-'·'_' 20자 이내).");
            var inputAudit = InputAuditJson.Build(inputPaths, data, model.DroppedEquipment);
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
            var initialUboltDiagnostics = SpcSeeding.PreconnectInitialUbolts(model, options, logger);
    
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
                        health2.DiagnosticCounts = ValidationReporting.CountDiagnostics(dtos2);
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
                            vr2 = ValidationReporting.BuildValidationResult(stageDiagnostics, conn2.GroupCount, m);
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
                        health.DiagnosticCounts = ValidationReporting.CountDiagnostics(pdtos);
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
                            validationResult = ValidationReporting.BuildValidationResult(stageDiagnostics, conn.GroupCount, m);
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
    
            ExcludedRigidAudit.Write(outDir, report.Stages.SelectMany(s => s.Diagnostics), model);

            foreach (var sr in report.Stages)
                foreach (var diag in sr.Diagnostics)
                    Console.WriteLine($"  [{diag.Severity}] {diag.Code}: {diag.Message}");
    
            bool hasError = report.Stages.SelectMany(s => s.Diagnostics)
                .Any(d => d.Severity == DiagnosticSeverity.Error);
    
            if (report.Succeeded)
            {
                var rbeDepSet = model.Rigids
                    .SelectMany(r => r.DependentNodeIds)
                    .ToHashSet();
                var bottomNodeIds = SpcSeeding.ResolveModelBottomNodes(model, legZTol).ToList();
                int dropped = 0;
                var spcSeed = bottomNodeIds
                    .Where(id =>
                    {
                        if (rbeDepSet.Contains(id)) { dropped++; return false; }
                        return true;
                    })
                    .Distinct()
                    .ToList();
                if (spcSeed.Count == 0)
                    spcSeed = SpcSeeding.ResolveModelBottomNodes(model, legZTol, rbeDepSet).ToList();
    
                AnalysisCase? analysis = null;
                IEnumerable<int> spcNodeIds = spcSeed;
                if (runNastran)
                {
                    // 각 connected group마다 최소 1개 SPC 보장 — 미구속 그룹은 mechanism으로
                    // 작용하여 KLL singular(FATAL 9050)를 일으킴
                    spcNodeIds = SpcSeeding.EnsureSpcPerGroup(model, spcSeed, rbeDepSet, out int addedForGroups);
                    Console.WriteLine(
                        $"SPC1: 모델 최저 Z±{legZTol:F1}mm {bottomNodeIds.Count}개, " +
                        $"RBE2 dep 제외 {dropped}개, group 커버리지 보강 {addedForGroups}개 " +
                        $"→ 최종 {spcNodeIds.Count()}개 노드 (123456)");
                    // 떨어진 그룹을 공중에서 고정하면 FATAL 9050 은 피하지만 그 그룹의 무게는 구조로 가지 않는다.
                    // 해석은 성공으로 끝나므로 조용히 넘기지 않고 경고로 남긴다.
                    if (addedForGroups > 0)
                        Console.WriteLine(
                            $"  [Warning] SPC_GROUP_COVERAGE: 주 구조와 연결되지 않은 그룹 {addedForGroups}개를 " +
                            "그룹 최저점 고정으로 해석했습니다 - 이 그룹들의 하중은 구조로 전달되지 않습니다. " +
                            "Studio 에서 연결을 확인하세요.");
                }
                else
                {
                    Console.WriteLine(
                        $"SPC1: 모델 최저 Z±{legZTol:F1}mm {bottomNodeIds.Count}개, " +
                        $"RBE2 dep 제외 {dropped}개 → 최종 {spcNodeIds.Count()}개 노드 (123456)");
                }
                if (spcNodeIds.Any())
                    analysis = new AnalysisCase();
    
                // 최종 산출물 파일명 — CSV 입력 파일명에서 struData/pipeData/equpData 토큰을 제거.
                // 예) "3468-35030-struData-A453412_20260108.csv" → "3468-35030-A453412_20260108.bdf"
                string designName = CsvInput.DeriveDesignName(baseName, inputPaths);
                var bdfFile  = Path.Combine(outDir, $"{designName}.bdf");
                var finalJsonFile = Path.Combine(outDir, $"{designName}.json");
    
                // ── SPC 태그 동기화 ──
                // 최종 BDF SPC1 노드 집합을 모델에 NodeTags.Spc 로 부여하고,
                // 이미 작성된 phase/detail JSON 에도 후처리로 "Spc" 태그를 주입한다.
                // 이후 BDF/Final JSON 출력은 자동으로 Spc 태그를 반영한다.
                var spcNodeIdSet = spcNodeIds.ToHashSet();
                int spcTagged = SpcTagging.ApplySpcTag(model, spcNodeIdSet);
                int spcJsonsPatched = SpcTagging.PatchPhaseJsonsWithSpcTag(outDir, spcNodeIdSet);
                spcNodeIds = spcNodeIdSet; // BDF Writer 가 다시 열거하므로 List 화
    
                // --ubolt-full-fix: 최종 BDF/JSON 모두 cm="123456" 으로 일치시킨다 (phase JSON 은 원본 보존)
                int uboltCmReplaced = uboltFullFix ? SpcTagging.ApplyUboltFullDof(model) : 0;
    
                using (var writer = new StreamWriter(bdfFile))
                    BdfWriter.Write(model, writer, spcNodeIds, analysis, uboltFullFix, options.PipePidBase);
    
                // 최종 모델 JSON — 사용자가 다운로드받을 산출물의 phase 단계와 동일 메타로 직렬화
                var finalConn   = ConnectivityAnalyzer.Analyze(model);
    
                // [진단 전용, 출력 불변] SPC 를 하나도 갖지 못한 연결 그룹(= 완전 무구속) 경고.
                // BDF/SPC 산출물은 전혀 바뀌지 않는다. EnsureSpcPerGroup(--run-nastran) 시엔 모든 그룹이
                // 보강되므로 이 경고는 비지 않고, --run-nastran 없이 빌드한 경우에만 발생할 수 있다.
                var groupsWithoutSpc = ConnectivityAnalyzer.GroupsWithoutSpc(finalConn, spcNodeIdSet);
                if (groupsWithoutSpc.Count > 0)
                {
                    Console.WriteLine(
                        $"[경고] SPC 미보장 연결 그룹 {groupsWithoutSpc.Count}개 — 외부 Nastran 해석 시 " +
                        "특이 강성행렬(FATAL 9050 계열) 위험. 예: " +
                        string.Join(", ", groupsWithoutSpc.Take(3).Select(g =>
                            $"[{string.Join("/", (g.NodeIds ?? new List<int>()).Take(4))}…] ({g.NodeCount}노드)")));
                }
    
                var finalDtos   = ModelDiagnosticsAnalyzer.Analyze(model, options.Tolerances);
                // 최종 JSON 진단은 모델 상태로 다시 계산해 단계 경고가 빠진다. Studio 는 Final 을 기본으로 열고
                // 편집도 Final 에서 하므로, 사용자가 꼭 봐야 하는 입력값 오류(장비 COG 위치)는 이어 붙인다.
                var rigidIds = model.Rigids.Select(r => r.Id).ToHashSet();
                finalDtos.AddRange(report.Stages.SelectMany(s => s.Diagnostics)
                    .Where(d => d.Code == "EQUIPMENT_COG_FAR" && d.ElementId is int id && rigidIds.Contains(id))
                    .Select(d => new DiagnosticDto
                    {
                        Severity = d.Severity.ToString().ToLowerInvariant(),
                        Code     = d.Code,
                        Message  = d.Message,
                        ElemId   = d.ElementId,
                        NodeId   = d.NodeId,
                    }));
                var finalHealth = HealthMetricsAnalyzer.Analyze(model,
                    report.Stages.SelectMany(s => s.Diagnostics).ToList(), options.Tolerances);
                finalHealth.DiagnosticCounts = ValidationReporting.CountDiagnostics(finalDtos);
                var finalValidation = ValidationReporting.BuildValidationResult(
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
    
                // StageSummary에 모델 전체 질량/무게중심 추가 후 재저장.
                // 원본(유체 포함) 과 유체 비움(배관 rho=강재) 두 변형을 함께 저장 —
                // Studio 는 로드 직후 유체 비움 상태를 기본으로 표시하므로, WorkBench 화면과 값이 어긋나지 않도록 한다.
                var massProps           = MassPropertiesAnalyzer.Analyze(model);
                var massPropsFluidEmpty = MassPropertiesAnalyzer.AnalyzeFluidEmpty(model);
                stageSummary.Summary.MassProperties = new StageSummaryMassProperties
                {
                    TotalMassTon      = massProps.TotalMassTon,
                    BeamMassTon       = massProps.BeamMassTon,
                    PointMassTon      = massProps.PointMassTon,
                    CenterOfGravityMm = new[] { massProps.CenterOfGravity.X, massProps.CenterOfGravity.Y, massProps.CenterOfGravity.Z },
                };
                stageSummary.Summary.MassPropertiesFluidEmpty = new StageSummaryMassProperties
                {
                    TotalMassTon      = massPropsFluidEmpty.TotalMassTon,
                    BeamMassTon       = massPropsFluidEmpty.BeamMassTon,
                    PointMassTon      = massPropsFluidEmpty.PointMassTon,
                    CenterOfGravityMm = new[] { massPropsFluidEmpty.CenterOfGravity.X, massPropsFluidEmpty.CenterOfGravity.Y, massPropsFluidEmpty.CenterOfGravity.Z },
                };
                File.WriteAllText(stageSummaryFile, stageSummary.ToJson());

                Console.WriteLine($"BDF:  {bdfFile}");
                Console.WriteLine($"JSON: {finalJsonFile}");
                Console.WriteLine($"Mass: {massProps.TotalMassTon * 1000:F1} kg " +
                    $"(beam {massProps.BeamMassTon * 1000:F1} + point {massProps.PointMassTon * 1000:F1}), " +
                    $"CG=({massProps.CenterOfGravity.X:F1}, {massProps.CenterOfGravity.Y:F1}, {massProps.CenterOfGravity.Z:F1}) mm");
                Console.WriteLine($"Mass (fluid-empty): {massPropsFluidEmpty.TotalMassTon * 1000:F1} kg " +
                    $"(beam {massPropsFluidEmpty.BeamMassTon * 1000:F1} + point {massPropsFluidEmpty.PointMassTon * 1000:F1})");
                if (uboltFullFix)
                {
                    int uboltCount = model.Rigids.Count(r => r.Remark == "UBOLT");
                    Console.WriteLine($"UBOLT cm → \"123456\" 강제 적용 (BDF/JSON 모두 반영, 변경 {uboltCmReplaced}/{uboltCount}개, --ubolt-full-fix)");
                }
                Console.WriteLine($"SPC 태그: {spcTagged}개 노드에 NodeTags.Spc 부여, phase/detail JSON {spcJsonsPatched}개 갱신");
    
                if (runNastran)
                    NastranRunner.RunNastran(bdfFile, nastranPath);
            }
    
            Console.WriteLine(report.Succeeded ? "파이프라인 완료." : "파이프라인 실패.");
            if (report.StoppedAfterStage is not null)
                Console.WriteLine($"--stopat '{report.StoppedAfterStage}' 에서 중단.");
    
            if (hasError)  return 2;
            if (!report.Succeeded) return 1;
            return 0;
            }
            catch (Exception ex)
            {
                // build-full 실행 경로(CSV 로드~최종 산출)에서 예상치 못한 예외가 발생하면
                // 원시 스택트레이스로 크래시하지 않고 명확한 [오류] 메시지 + 실패 exit code(1)로 변환한다.
                // 성공 경로의 0/2/1 계약은 그대로 유지된다(백엔드가 이미 처리하는 실패 코드 1 사용).
                // 이미 "출력 폴더:" 가 출력된 뒤일 수 있으므로, 백엔드가 폴더만 보고 성공으로
                // 오인하지 않도록 stderr 로 실패를 분명히 남긴다.
                Console.Error.WriteLine($"[오류] build-full 실행 중 예외: {ex.Message}");
                return 1;
            }
        });
    
        return cmd;
    }
    
    // ── apply-edit-intent ───────────────────────────────────────────────────────
    
    internal static Command BuildApplyEditIntentCommand()
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
            return EditIntentApplier.ApplyEditIntent(folder, outDir, strict);
        });
    
        return cmd;
    }
}
