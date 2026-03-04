using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using _2025_Skid.Model;


namespace _2025_Skid.Model
{
  public struct RbeAttribute
  {
    public int IndependentNodeID;
    public int[] DependentNodesID;
    public Dictionary<string, string> ExtraData { get; set; }

    public RbeAttribute(int independenNodeID, int[] dependentNodesID, Dictionary<string, string> extraData = null)
    {
      IndependentNodeID = independenNodeID;
      DependentNodesID = dependentNodesID;
      ExtraData = extraData ?? new Dictionary<string, string>();
    }

    public override string ToString()
    {
      string extraInfo = (ExtraData != null && ExtraData.Count > 0)
    ? string.Join(", ", ExtraData.Select(kv => $"{kv.Key}: {kv.Value}"))
    : "None";
      return $"independenNodeID:{IndependentNodeID}, DependentNodesID:{string.Join(", ", DependentNodesID)}, ExtraData: {extraInfo}  ";
    }
  }

  public class RBEs : IEnumerable<KeyValuePair<int, RbeAttribute>>
  {
    public Elements Elements;
    public int RbeID = 0;
    public Dictionary<int, RbeAttribute> Rbes = new Dictionary<int, RbeAttribute>(); // 요소 저장
    public List<int> RbeNodes = new List<int>();

    public RBEs(Elements elements)
    {
      Elements = elements;
    }

    // RBE를 새로 생성하면 자동으로 RbeID를 생성하고 이를 return 한다. 
    public int AddOrGet(int independentID, int[] dependencNodesID, Dictionary<string, string> extraData = null)
    {
      if (dependencNodesID == null || dependencNodesID.Length == 0)
        throw new ArgumentException("dependencNodesID must have at least one node.");

      // 정규화: 중복 제거 + 정렬
      int[] depsNorm = dependencNodesID.Distinct().OrderBy(x => x).ToArray();

      // 동일 dependent 세트가 이미 있는지 검사
      foreach (var kv in Rbes)
      {
        // NOTE: RbeAttribute가 struct라서 kv.Value?. 사용 불가 → 그냥 kv.Value.
        // 배열만 null일 수 있으므로 ?? Array.Empty<int>() 처리
        var existingDeps = kv.Value.DependentNodesID ?? Array.Empty<int>();
        var existingNorm = existingDeps.Distinct().OrderBy(x => x).ToArray();

        if (existingNorm.SequenceEqual(depsNorm))
          return kv.Key; // 동일 세트 → 기존 ID 반환
      }

      // 없으면 새로 추가 (요청한 형태 유지)
      RbeID = (Rbes.Keys.Count > 0) ? Rbes.Keys.Max() + 1 : 100001; // RBE는 100000번대부터 시작
      Rbes[RbeID] = new RbeAttribute(independentID, depsNorm, extraData);

      RbeNodes.Add(independentID);
      RbeNodes.AddRange(depsNorm);

      // Elements.elementID를 갱신하지 말 것!
      return RbeID;
    }



    // Equivalence 결과(old→rep)로 하나의 RBE 종속 노드들을 치환
    public bool RemapDependentNodes(int rbeId, Dictionary<int, int> oldToRep, bool dropIfEmpty = true)
    {
      if (!Rbes.TryGetValue(rbeId, out var attr))
        return false;

      int gn = attr.IndependentNodeID;

      // 노드 치환(없으면 그대로), GN과 동일한 노드 제거, 중복 제거
      var remapped = attr.DependentNodesID
          .Select(gk => oldToRep.TryGetValue(gk, out var rep) ? rep : gk)
          .Where(gk => gk > 0 && gk != gn)
          .Distinct()
          .ToArray();

      if (dropIfEmpty && remapped.Length == 0)
      {
        // 더 이상 유효한 종속 노드가 없으면 이 RBE 제거(정책에 따라 변경 가능)
        Rbes.Remove(rbeId);
        return false;
      }

      Rbes[rbeId] = new RbeAttribute(gn, remapped);
      return true;
    }

    // 모든 RBE에 대해 일괄 치환
    public void RemapAllDependents(Dictionary<int, int> oldToRep, bool dropIfEmpty = true)
    {
      // 키 목록을 별도 복사(중간에 Remove 가능)
      var ids = Rbes.Keys.ToList();
      foreach (var rid in ids)
        RemapDependentNodes(rid, oldToRep, dropIfEmpty);
    }



    public void SynchronizeRbeIDWithElements()
    {
      // ElementID는 부재와 RBE가 연동되기에 기존의 Elements에서 최대 elementID를 받아와서 거기서 시작한다. 
      int maxElementID = Elements.GetLastID();
      RbeID = ++maxElementID;
    }

    public IEnumerator<KeyValuePair<int, RbeAttribute>> GetEnumerator()
    {
      return Rbes.GetEnumerator();
    }

    // IEnumerable 인터페이스 구현 (비제네릭 버전)
    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }
  }
}
