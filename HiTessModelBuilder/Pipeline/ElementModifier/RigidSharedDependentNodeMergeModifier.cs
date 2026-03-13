using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Nastran 자유도 충돌 방지]
  /// 여러 강체(RBE)가 동일한 종속 노드(Dependent Node)를 공유할 때 발생하는 Nastran 오류를 방지하기 위해,
  /// 공유되는 종속 노드를 마스터(Independent)로 뒤집고, 기존 마스터들을 종속(Dependent)으로 묶어 1개의 강체로 병합합니다.
  /// </summary>
  public static class RigidSharedDependentNodeMergeModifier
  {
    public static int Run(FeModelContext context, bool pipelineDebug = true, bool verboseDebug = true, Action<string>? log = null)
    {
      log ??= Console.WriteLine;
      int mergedCount = 0;

      // 1. Dependent Node ID를 Key로 하여, 이를 참조하는 Rigid ID들의 리스트를 수집
      var depNodeToRigidIds = new Dictionary<int, List<int>>();

      foreach (var kvp in context.Rigids)
      {
        foreach (int depId in kvp.Value.DependentNodeIDs)
        {
          if (!depNodeToRigidIds.ContainsKey(depId))
            depNodeToRigidIds[depId] = new List<int>();
          depNodeToRigidIds[depId].Add(kvp.Key);
        }
      }

      // 2. 2개 이상의 Rigid가 공유하는 Dependent Node 찾기
      var sharedDepNodes = depNodeToRigidIds.Where(kv => kv.Value.Count >= 2).ToList();

      if (sharedDepNodes.Count == 0) return 0;

      if (pipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] RigidSharedDependentNodeMergeModifier (공유 종속 노드 병합)");
        log($" -> 중복 참조된 Dependent Node 개수: {sharedDepNodes.Count}개");
        log($"==================================================\n");
      }

      foreach (var kvp in sharedDepNodes)
      {
        int sharedDepNodeId = kvp.Key;
        // 기존 요소가 다른 병합 과정에서 지워졌을 수도 있으므로 Contains 로 필터링
        var conflictingRigidIds = kvp.Value.Where(id => context.Rigids.Contains(id)).ToList();

        if (conflictingRigidIds.Count < 2) continue;

        var newDependentNodes = new List<int>();
        var mergedExtraData = new Dictionary<string, string>();
        var restChars = new HashSet<char>(); // ★ 자유도를 합집합으로 모을 해시셋

        // 3. 기존 Rigid들을 지우고 Independent Node들을 수집하여 종속 노드로 변환
        foreach (int rId in conflictingRigidIds)
        {
          var rigid = context.Rigids[rId];
          newDependentNodes.Add(rigid.IndependentNodeID);

          // 기존의 다른 Dependent Node들도 유실되지 않도록 새 Dependent에 추가 (공유 노드는 제외)
          foreach (int otherDep in rigid.DependentNodeIDs)
          {
            if (otherDep != sharedDepNodeId)
              newDependentNodes.Add(otherDep);
          }

          // ★ 수정됨: rigid.Rest 가 아니라 rigid.Cm 을 사용합니다.
          // 자유도(Cm) 문자열을 분해해서 고유한 숫자만 수집 (합집합 처리)
          if (!string.IsNullOrWhiteSpace(rigid.Cm))
          {
            foreach (char c in rigid.Cm)
            {
              if (char.IsDigit(c)) restChars.Add(c);
            }
          }

          // ExtraData는 마지막 강체 기준으로 병합
          mergedExtraData = rigid.ExtraData?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, string>();

          // 기존 강체는 모델에서 영구 삭제
          context.Rigids.Remove(rId);
        }

        // ★ 수집된 자유도 숫자들을 오름차순(123456)으로 정렬하여 새로운 Cm 문자열 생성
        string mergedCm = new string(restChars.OrderBy(c => c).ToArray());
        if (string.IsNullOrWhiteSpace(mergedCm)) mergedCm = "123456"; // 혹시 비어있다면 기본 강체 보장

        // 속성에 병합되었다는 흔적 남기기
        mergedExtraData["Remark"] = "MERGED_SHARED_DEP";
        newDependentNodes = newDependentNodes.Distinct().ToList();

        // 4. 마스터-슬레이브 관계를 뒤집어서 새로운 강체 1개로 생성 (mergedCm 사용)
        int newRigidId = context.Rigids.AddOrGet(sharedDepNodeId, newDependentNodes, mergedCm, mergedExtraData);
        mergedCount++;

        if (verboseDebug)
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          log($"[병합 완료] 공유 노드 N{sharedDepNodeId} 중심 병합 (삭제된 기존 RBE: {string.Join(", ", conflictingRigidIds)}) -> 신규 RBE {newRigidId} 생성 (자유도 합집합: {mergedCm})");
          Console.ResetColor();
        }
      }

      if (pipelineDebug)
        log($"[수정 완료] 총 {mergedCount}개의 중복 종속 노드 병합이 완료되었습니다.\n");

      return mergedCount;
    }
  }
}