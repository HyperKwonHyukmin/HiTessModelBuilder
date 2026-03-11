using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Logging;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Services.Debugging
{
  public static class AuditTracker
  {
    public static void GenerateFinalAuditReport(RawCsvDesignData rawData, FeModelContext context, PipelineLogger logger)
    {
      if (rawData == null) return;

      // 1. 원본 CSV에 존재했던 모든 Name 수집
      var initialNames = new HashSet<string>();

      // 구조물(Stru) Name 수집
      if (rawData.AngDesignList != null) foreach (var item in rawData.AngDesignList) initialNames.Add(item.Name);
      if (rawData.BeamDesignList != null) foreach (var item in rawData.BeamDesignList) initialNames.Add(item.Name);
      if (rawData.BscDesignList != null) foreach (var item in rawData.BscDesignList) initialNames.Add(item.Name);
      if (rawData.BulbDesignList != null) foreach (var item in rawData.BulbDesignList) initialNames.Add(item.Name);
      if (rawData.FbarDesignList != null) foreach (var item in rawData.FbarDesignList) initialNames.Add(item.Name);
      if (rawData.RbarDesignList != null) foreach (var item in rawData.RbarDesignList) initialNames.Add(item.Name);
      if (rawData.TubeDesignList != null) foreach (var item in rawData.TubeDesignList) initialNames.Add(item.Name);

      // 배관(Pipe) 및 장비(Equip) Name 수집
      if (rawData.PipeList != null) foreach (var item in rawData.PipeList) initialNames.Add(item.Name);
      if (rawData.EquipList != null) foreach (var item in rawData.EquipList) initialNames.Add(item.Name);

      // 2. 최종 FE 모델(Context)에 살아남은 Name 수집
      var survivedNames = new HashSet<string>();

      // Elements 순회
      foreach (var kvp in context.Elements)
      {
        var e = kvp.Value;
        if (e.ExtraData != null)
        {
          if (e.ExtraData.TryGetValue("ID", out string idVal)) survivedNames.Add(idVal);
          if (e.ExtraData.TryGetValue("Name", out string nameVal)) survivedNames.Add(nameVal);
        }
      }

      // PointMasses 순회
      foreach (var kvp in context.PointMasses)
      {
        var pm = kvp.Value;
        if (pm.ExtraData != null && pm.ExtraData.TryGetValue("Name", out string nameVal)) survivedNames.Add(nameVal);
      }

      // Rigids 순회
      foreach (var kvp in context.Rigids)
      {
        var r = kvp.Value;
        if (r.ExtraData != null && r.ExtraData.TryGetValue("Name", out string nameVal)) survivedNames.Add(nameVal);
      }

      // 3. 차집합(Except)을 이용해 누락된(사라진) Name 색출
      var missingNames = initialNames.Except(survivedNames).OrderBy(n => n).ToList();

      // 4. 리포트 출력
      logger.LogInfo("\n==================================================");
      if (missingNames.Count == 0)
      {
        logger.LogSuccess("[최종 데이터 감사] 모든 원본 데이터가 FE 모델에 100% 반영되어 살아남았습니다.");
      }
      else
      {
        logger.LogWarning($"[최종 데이터 감사] 사용자 입력 데이터 중 총 {missingNames.Count}개가 최종 모델에서 누락/삭제되었습니다.");
        logger.LogWarning("   (상세 원인은 파이프라인 이전 로그의 [파싱 누락], [생성 누락], [영구 삭제] 내역을 참조하세요)");

        // 누락된 이름들을 5개씩 묶어서 예쁘게 출력
        for (int i = 0; i < missingNames.Count; i += 5)
        {
          var chunk = missingNames.Skip(i).Take(5);
          logger.LogWarning($"    - {string.Join(", ", chunk)}");
        }
      }
      logger.LogInfo("==================================================\n");
    }
  }
}
