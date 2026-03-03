using System;

namespace HiTessModelBuilder.Pipeline.Core
{
  /// <summary>
  /// 파이프라인에서 실행될 개별 스테이지의 정보와 실행 동작(Action)을 정의합니다.
  /// </summary>
  public class PipelineStage
  {
    /// <summary>
    /// 스테이지의 고유 이름 (예: "STAGE_01")
    /// </summary>
    public string StageName { get; }

    /// <summary>
    /// 해당 스테이지에서 수행할 핵심 로직. 
    /// 매개변수: (bool pipelineDebug, bool verboseDebug)
    /// </summary>
    public Action<bool, bool> ExecuteAction { get; }

    /// <summary>
    /// PipelineStage 생성자
    /// </summary>
    /// <param name="stageName">스테이지 이름</param>
    /// <param name="executeAction">실행할 동작 로직</param>
    public PipelineStage(string stageName, Action<bool, bool> executeAction)
    {
      StageName = stageName;
      ExecuteAction = executeAction;
    }
  }
}