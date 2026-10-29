// M-1 리팩터: Program.cs 에서 분리한 Nastran 실행 + F06 FATAL 보고. 동작 불변.

internal static class NastranRunner
{
    internal static void RunNastran(string bdfPath, string nastranPath)
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
    
    internal static void ReportFatalsFromF06(string f06Path)
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

        if (fatalCount == 0)
            ReportLoadBalanceFromF06(File.ReadAllText(f06Path));
    }

    // 자중 하중(OLOAD 합력)과 지점 반력(SPCFORCE 합력)의 Z 성분이 맞는지 본다.
    // 어긋나면 하중 일부가 AUTOSPC(자동 구속)로 빠진 것 — 해석은 '성공'이어도 하중 경로가 틀린 모델이다.
    internal static void ReportLoadBalanceFromF06(string f06Text)
    {
        double? oload = ResultantFz(f06Text, "OLOAD");
        double? spcf  = ResultantFz(f06Text, "SPCFORCE");
        int autoSpc = System.Text.RegularExpressions.Regex.Matches(
            f06Text, @"^\s+\d+\s+G\s+\d\s+\S+\s+B", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

        if (oload is null || spcf is null)
        {
            Console.WriteLine("[Nastran] 하중·반력 합력 표를 찾지 못해 평형 점검을 건너뜁니다.");
            return;
        }

        double weight = Math.Abs(oload.Value);
        double imbalance = weight > 0 ? Math.Abs(oload.Value + spcf.Value) / weight : 0.0;
        Console.WriteLine(
            $"[Nastran] 자중 합력 Fz {oload.Value:E4} N, 지점 반력 합력 Fz {spcf.Value:E4} N " +
            $"(불균형 {imbalance * 100:F2}%), 자동 구속(AUTOSPC) {autoSpc}개");
        if (imbalance > 0.01)
            Console.WriteLine($"  [Warning] NASTRAN_LOAD_IMBALANCE: 자중의 {imbalance * 100:F1}% 가 지점 반력으로 돌아오지 않습니다 - 하중 경로를 확인하세요.");
        if (autoSpc > 0)
            Console.WriteLine($"  [Warning] NASTRAN_AUTOSPC: {autoSpc}개 자유도가 자동 구속되었습니다(강성 없는 자유도) - 연결 누락 가능성.");
    }

    private static double? ResultantFz(string f06Text, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            f06Text, tag + @"\s+RESULTANT.*?TOTALS\s+(\S+)\s+(\S+)\s+(\S+)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return m.Success && double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
