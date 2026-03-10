using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [강체 종속성 치명적 오류 힐링]
  /// 1. 순환 종속 (Circular Dependency): A->B->A 꼬리물기 발생 시 백엣지(Back-edge)를 절단합니다.
  /// 2. 다중 종속 (Double Dependency): 여러 마스터가 하나의 슬레이브를 공유할 경우 하나로 통폐합합니다.
  /// </summary>
  public static class RigidConsolidationModifier
  {
    public sealed record Options(
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int totalFixed = 0;

      // 1차: 순환 꼬리물기 파괴 (DFS 탐색)
      totalFixed += BreakCircularDependencies(context, opt, log);

      // 2차: 중 종속 통폐합
      totalFixed += ConsolidateDoubleDependencies(context, opt, log);

      // 3차: 통폐합 과정에서 우연히 또 순환이 생겼을 수 있으므로 확인 사살
      totalFixed += BreakCircularDependencies(context, opt, log);

      return totalFixed;
    }

    /// <summary>
    /// A -> B -> A 형태의 꼬리물기를 찾아내어 물리적 결속은 유지하되 역방향 참조만 끊어냅니다.
    /// </summary>
    private static int BreakCircularDependencies(FeModelContext context, Options opt, Action<string> log)
    {
      int brokenCount = 0;
      bool cycleFound;

      do
      {
        cycleFound = false;

        // 1. 방향성 그래프(Directed Graph) 구축
        var adj = new Dictionary<int, List<(int v, int rbeId)>>();
        foreach (var kvp in context.Rigids)
        {
          int u = kvp.Value.IndependentNodeID;
          if (!adj.ContainsKey(u)) adj[u] = new List<(int, int)>();

          foreach (int v in kvp.Value.DependentNodeIDs)
          {
            adj[u].Add((v, kvp.Key));
          }
        }

        var visited = new HashSet<int>();
        var recStack = new HashSet<int>();

        // 2. DFS(깊이 우선 탐색)로 백엣지(Back-edge) 찾기
        bool DFS(int u)
        {
          if (recStack.Contains(u)) return false;
          if (visited.Contains(u)) return false;

          visited.Add(u);
          recStack.Add(u);

          if (adj.ContainsKey(u))
          {
            foreach (var edge in adj[u])
            {
              int v = edge.v;
              int rbeId = edge.rbeId;

              if (recStack.Contains(v))
              {
                if (context.Rigids.Contains(rbeId))
                {
                  var rbe = context.Rigids[rbeId];

                  // 1. 기존 종속 노드에서 꼬리물기 노드(v)만 제외한 새로운 리스트 생성
                  var newDeps = rbe.DependentNodeIDs.Where(id => id != v).ToList();
                  brokenCount++;

                  if (opt.VerboseDebug)
                    log($"   -> [순환 파괴] N{u} -> N{v} 꼬리물기 감지! RBE {rbeId}의 역방향 종속을 끊어 해결했습니다.");

                  // 2. 만약 남은 슬레이브가 0개라면 빈 깡통 RBE를 완전히 삭제
                  if (newDeps.Count == 0)
                  {
                    context.Rigids.Remove(rbeId);
                  }
                  else
                  {
                    // 3. 슬레이브가 남아있다면, Rigids 컬렉션의 AddWithID를 사용하여 불변 객체를 안전하게 덮어쓰기
                    var extraCopy = rbe.ExtraData.ToDictionary(k => k.Key, val => val.Value);
                    context.Rigids.AddWithID(rbeId, rbe.IndependentNodeID, newDeps, rbe.Cm, extraCopy);
                  }
                }
                return true; // 하나 끊었으니 그래프 다시 그리러 탈출
              }

              if (DFS(v)) return true;
            }
          }

          recStack.Remove(u);
          return false;
        }

        foreach (var node in adj.Keys)
        {
          if (DFS(node))
          {
            cycleFound = true;
            break;
          }
        }

      } while (cycleFound); // 더 이상 꼬리물기가 없을 때까지 무한 반복

      if (opt.PipelineDebug && brokenCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Magenta;
        log($"[변경] 순환 종속(Circular Dependency) 방지 : {brokenCount}건의 꼬리물기 링크를 안전하게 절단했습니다.");
        Console.ResetColor();
      }

      return brokenCount;
    }

    /// <summary>
    /// 여러 대장(Master)이 하나의 부하(Slave)를 공유할 때, 하나의 거대 RBE로 통폐합합니다.
    /// </summary>
    private static int ConsolidateDoubleDependencies(FeModelContext context, Options opt, Action<string> log)
    {
      int consolidatedCount = 0;
      bool mergedAny;

      do
      {
        mergedAny = false;
        var depNodeToRbes = new Dictionary<int, List<int>>();

        foreach (var kvp in context.Rigids)
        {
          foreach (int depNodeId in kvp.Value.DependentNodeIDs)
          {
            if (!depNodeToRbes.ContainsKey(depNodeId))
              depNodeToRbes[depNodeId] = new List<int>();
            depNodeToRbes[depNodeId].Add(kvp.Key);
          }
        }

        // 2개 이상의 RBE가 공유하는 '충돌 노드' 찾기
        var conflictDepNodes = depNodeToRbes.Where(kv => kv.Value.Count > 1).ToList();

        if (conflictDepNodes.Count > 0)
        {
          mergedAny = true;
          var conflict = conflictDepNodes.First();
          int sharedNodeId = conflict.Key;
          var conflictingRbeIds = conflict.Value;

          var newDepNodes = new HashSet<int>();
          var allExtraData = new Dictionary<string, string>();

          foreach (int rbeId in conflictingRbeIds)
          {
            if (!context.Rigids.Contains(rbeId)) continue;
            var rbe = context.Rigids[rbeId];

            if (rbe.IndependentNodeID != sharedNodeId)
              newDepNodes.Add(rbe.IndependentNodeID);

            foreach (int depId in rbe.DependentNodeIDs)
            {
              if (depId != sharedNodeId)
                newDepNodes.Add(depId);
            }

            if (rbe.ExtraData != null)
            {
              foreach (var extra in rbe.ExtraData)
                allExtraData[extra.Key] = extra.Value;
            }
            context.Rigids.Remove(rbeId);
          }

          allExtraData["Remark"] = "Consolidated_RBE";

          int newRbeId = context.Rigids.AddNew(
              independentNodeID: sharedNodeId,
              dependentNodeIDs: newDepNodes.ToList(),
              cm: "123456",
              extraData: allExtraData
          );

          consolidatedCount++;
          if (opt.VerboseDebug)
          {
            log($"   -> [강체 통폐합] N{sharedNodeId}를 공유하던 RBE {string.Join(", ", conflictingRbeIds)} -> 통합 RBE {newRbeId} 생성");
          }
        }
      } while (mergedAny);

      if (opt.PipelineDebug && consolidatedCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[변경] 이중 종속(Double Dependency) 방지 : 동일한 노드를 공유하던 강체(RBE) 충돌 {consolidatedCount}건을 통폐합했습니다.");
        Console.ResetColor();
      }

      return consolidatedCount;
    }
  }
}
