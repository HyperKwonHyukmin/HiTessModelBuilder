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

// stdout/stderr를 수집하는 서버의 Windows 코드 페이지와 무관하게 UTF-8로 출력한다.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
rootCommand.Add(CliCommands.BuildParseCommand());
rootCommand.Add(CliCommands.BuildBuildRawCommand());
rootCommand.Add(CliCommands.BuildBuildFullCommand());
rootCommand.Add(CliCommands.BuildApplyEditIntentCommand());
rootCommand.SetAction(parseResult =>
{
    var folder = parseResult.GetValue(rootApplyEditIntentOption);
    if (folder is null)
    {
        Console.Error.WriteLine("[오류] 명령을 지정하세요. 예: build-full 또는 --apply-edit-intent <폴더>");
        return 1;
    }

    return EditIntentApplier.ApplyEditIntent(
        folder,
        parseResult.GetValue(rootApplyEditOutOption),
        parseResult.GetValue(rootApplyEditStrictOption));
});
return rootCommand.Parse(args).Invoke();
