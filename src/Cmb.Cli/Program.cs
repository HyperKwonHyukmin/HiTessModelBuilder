using System.CommandLine;
using Cmb.Core.Building;
using Cmb.Core.Model;
using Cmb.Core.Model.Raw;
using Cmb.Core.Serialization;
using Cmb.Io.Csv;
using Cmb.Io.Nastran;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using Microsoft.Extensions.Logging;

var rootCommand = new RootCommand("ClaudeModelBuilder — AM CSV → Nastran BDF 변환기");
rootCommand.Add(BuildParseCommand());
rootCommand.Add(BuildBuildRawCommand());
rootCommand.Add(BuildBuildFullCommand());
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

static (RawDesignData? data, string baseName, string csvDir, string? error) LoadCsv(
    DirectoryInfo? input, FileInfo? stru, FileInfo? pipe, FileInfo? equip)
{
    var loader = new CsvDesignLoader();

    if (stru is not null || pipe is not null || equip is not null)
    {
        var firstFile = (stru ?? pipe ?? equip)!;
        var baseName  = firstFile.Name.Replace(".csv", "", StringComparison.OrdinalIgnoreCase);
        var csvDir    = firstFile.DirectoryName ?? ".";
        return (loader.Load(stru?.FullName, pipe?.FullName, equip?.FullName), baseName, csvDir, null);
    }

    if (input is not null)
    {
        if (!input.Exists)
            return (null, "", "", $"[오류] 입력 폴더를 찾을 수 없습니다: {input.FullName}");
        return (loader.LoadFolder(input.FullName), input.Name, input.FullName, null);
    }

    return (null, "", "", "[오류] --input 폴더 또는 --stru/--pipe/--equip 파일 중 하나 이상을 지정하세요.");
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
        var (data, baseName, csvDir, error) = LoadCsv(
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
        var (data, baseName, csvDir, error) = LoadCsv(
            parseResult.GetValue(inputOption),
            parseResult.GetValue(struOption),
            parseResult.GetValue(pipeOption),
            parseResult.GetValue(equipOption));

        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var outDir = MakeOutputDir(csvDir);

        foreach (var skip in data!.Skips)
            Console.Error.WriteLine($"[SKIP] {skip.Kind} line {skip.LineNumber} '{skip.Name}': {skip.Reason}");

        var model = new FeModelBuilder().Build(data);

        var jsonFile = Path.Combine(outDir, $"{baseName}.initial.json");
        File.WriteAllText(jsonFile, model.ToJson(phase: "B", stageName: "initial"));

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

    var (inputOption, struOption, pipeOption, equipOption) = MakeCsvOptions();

    var cmd = new Command("build-full", "CSV → FeModel → 파이프라인 스테이지 실행 (스테이지별 JSON + BDF 출력)");
    cmd.Add(inputOption);
    cmd.Add(struOption);
    cmd.Add(pipeOption);
    cmd.Add(equipOption);
    cmd.Add(stopatOption);

    cmd.SetAction(parseResult =>
    {
        var (data, baseName, csvDir, error) = LoadCsv(
            parseResult.GetValue(inputOption),
            parseResult.GetValue(struOption),
            parseResult.GetValue(pipeOption),
            parseResult.GetValue(equipOption));

        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var stopAt = parseResult.GetValue(stopatOption);
        var outDir  = MakeOutputDir(csvDir);
        Console.WriteLine($"출력 폴더: {outDir}");

        foreach (var skip in data!.Skips)
            Console.Error.WriteLine($"[SKIP] {skip.Kind} line {skip.LineNumber} '{skip.Name}': {skip.Reason}");

        var model = new FeModelBuilder().Build(data);

        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger  = loggerFactory.CreateLogger("Pipeline");
        var options = new RunOptions(new Tolerances(), StopAfterStage: stopAt);

        IPipelineStage[] stages =
        [
            new SanityPreprocessStage(),
            new MeshingStage(),
            new NodeEquivalenceStage(),
            new IntersectionStage(),
            new DanglingShortRemoveStage(),
            new CollinearNodeMergeStage(),
            new ExtendToIntersectStage(),           // 자유단 → 인접 부재 연장 연결
            new SplitByExistingNodesStage(),         // ExtendToIntersect pSeg 노드 → 타겟 부재 분할
            new GroupConnectStage(),
            new SplitByExistingNodesStage(),         // GroupConnect snap 노드 → 구조 부재 분할
            new UboltRbeStage(),
            new SplitByExistingNodesStage(),         // UboltRbe snap 노드 → 구조 부재 분할
            new FinalValidationStage(),
        ];

        int idx = 0;
        var report = PipelineRunner.Run(model, stages, options, logger,
            onStageComplete: (stageName, m, stageDiagnostics) =>
            {
                idx++;
                var connectivity  = ConnectivityAnalyzer.Analyze(m);
                var healthMetrics = HealthMetricsAnalyzer.Analyze(m, stageDiagnostics, options.Tolerances);
                var prefix   = Path.Combine(outDir, $"{idx:D2}_{stageName}");
                var jsonFile = prefix + ".json";
                File.WriteAllText(jsonFile, m.ToJson(phase: "C", stageName: stageName,
                    connectivity: connectivity, healthMetrics: healthMetrics));

                var bdfFile = prefix + ".bdf";
                using (var writer = new StreamWriter(bdfFile))
                    BdfWriter.Write(m, writer);

                var pct = connectivity.LargestGroupNodeRatio * 100;
                Console.WriteLine($"  [연결성] 그룹 {connectivity.GroupCount}개 | " +
                                  $"최대 {connectivity.LargestGroupElementCount}요소 ({pct:F1}%) | " +
                                  $"고립노드 {connectivity.IsolatedNodeCount}개");
                var iss = healthMetrics.Issues;
                Console.WriteLine($"  [건전성] 자유단 {iss.FreeEndNodes} · 고립 {iss.OrphanNodes} · " +
                                  $"짧은 {iss.ShortElements} · 미해결U볼트 {iss.UnresolvedUbolts} · " +
                                  $"분리그룹 {iss.DisconnectedGroups}");
                Console.WriteLine($"  → {jsonFile}");
                Console.WriteLine($"  → {bdfFile}");
            });

        foreach (var sr in report.Stages)
            foreach (var diag in sr.Diagnostics)
                Console.WriteLine($"  [{diag.Severity}] {diag.Code}: {diag.Message}");

        bool hasError = report.Stages.SelectMany(s => s.Diagnostics)
            .Any(d => d.Severity == DiagnosticSeverity.Error);

        if (report.Succeeded)
        {
            var spcNodeIds = model.Nodes
                .Where(n => n.HasTag(NodeTags.Boundary))
                .Select(n => n.Id);

            var bdfFile = Path.Combine(outDir, $"{baseName}.final.bdf");
            using (var writer = new StreamWriter(bdfFile))
                BdfWriter.Write(model, writer, spcNodeIds);

            Console.WriteLine($"BDF:  {bdfFile}");
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
