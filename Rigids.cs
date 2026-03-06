using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 단일 강체 요소(RBE2, RBE3 등) 데이터를 표현하는 불변(Immutable) 클래스입니다.
  /// (기존 RbeAttribute 구조체를 객체 지향적으로 개선)
  /// </summary>
  public sealed class RigidInfo
  {
    public int IndependentNodeID { get; }
    public IReadOnlyList<int> DependentNodeIDs { get; }
    public string Cm { get; }
    public IReadOnlyDictionary<string, string> ExtraData { get; }

    public RigidInfo(
        int independentNodeID,
        IEnumerable<int> dependentNodeIDs,
        string cm = "123456",
        Dictionary<string, string>? extraData = null)
    {
      if (dependentNodeIDs == null)
        throw new ArgumentNullException(nameof(dependentNodeIDs));

      IndependentNodeID = independentNodeID;

      // 종속 노드 정규화: 중복 제거 및 오름차순 정렬 (비교를 용이하게 함)
      DependentNodeIDs = dependentNodeIDs.Distinct().OrderBy(id => id).ToList().AsReadOnly();
      Cm = cm;

      ExtraData = extraData != null
        ? new Dictionary<string, string>(extraData)
        : new Dictionary<string, string>();
    }

    public override string ToString()
    {
      string extraInfo = ExtraData.Count > 0
        ? string.Join(", ", ExtraData.Select(kv => $"{kv.Key}:{kv.Value}"))
        : "None";
      return $"Indep:{IndependentNodeID}, Dep:[{string.Join(",", DependentNodeIDs)}], CM:{Cm}, ExtraData:{{{extraInfo}}}";
    }
  }

  /// <summary>
  /// 모델 내의 모든 강체(Rigid) 요소를 관리하는 컬렉션 클래스입니다.
  /// </summary>
  public class Rigids : IEnumerable<KeyValuePair<int, RigidInfo>>
  {
    private readonly Dictionary<int, RigidInfo> _rigids = new();

    // Nastran에서 CBEAM과 Element ID 충돌을 막기 위해 9,000,001번부터 발급
    private int _nextRigidID = 9000001;
    public int LastRigidID { get; private set; } = 0;

    public RigidInfo this[int id]
    {
      get
      {
        if (!_rigids.TryGetValue(id, out var rigid))
          throw new KeyNotFoundException($"Rigid ID {id} does not exist.");
        return rigid;
      }
    }

    public int Count => _rigids.Count;
    public IEnumerable<int> Keys => _rigids.Keys;

    /// <summary>
    /// 새로운 강체 요소를 무조건 추가하고 ID를 반환합니다.
    /// </summary>
    public int AddNew(int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456", Dictionary<string, string>? extraData = null)
    {
      int newID = _nextRigidID++;
      _rigids[newID] = new RigidInfo(independentNodeID, dependentNodeIDs, cm, extraData);
      LastRigidID = newID;
      return newID;
    }

    /// <summary>
    /// 동일한 종속 노드 구성을 가진 RBE가 있는지 확인하고, 없으면 새로 생성하여 ID를 반환합니다. 
    /// (RBEs_REF.cs 기능 통합)
    /// </summary>
    public int AddOrGet(int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456", Dictionary<string, string>? extraData = null)
    {
      var newRigid = new RigidInfo(independentNodeID, dependentNodeIDs, cm, extraData);

      // 이미 동일한 GN(Independent)과 GM(Dependent) 구성을 가진 객체가 있는지 확인
      foreach (var kv in _rigids)
      {
        if (kv.Value.IndependentNodeID == independentNodeID &&
            kv.Value.DependentNodeIDs.SequenceEqual(newRigid.DependentNodeIDs))
        {
          return kv.Key; // 동일 세트가 존재하면 기존 ID 반환
        }
      }

      int newID = _nextRigidID++;
      _rigids[newID] = newRigid;
      LastRigidID = newID;
      return newID;
    }

    /// <summary>
    /// 노드 병합(Equivalence) 결과를 반영하여 강체 요소의 마스터(Independent) 및 슬레이브(Dependent) 노드 ID를 모두 치환합니다.
    /// </summary>
    public bool RemapNodes(int rigidId, IReadOnlyDictionary<int, int> oldToRep, bool dropIfEmpty = true)
    {
      if (!_rigids.TryGetValue(rigidId, out var info))
        return false;

      // 1. Independent Node 갱신 (치환 대상에 있으면 바꾸고, 없으면 기존 값 유지)
      int newGn = oldToRep.TryGetValue(info.IndependentNodeID, out var repGn) ? repGn : info.IndependentNodeID;

      // 2. Dependent Nodes 갱신 (마스터 노드와 같아진 슬레이브 노드는 스스로 제거)
      var remappedDeps = info.DependentNodeIDs
          .Select(gk => oldToRep.TryGetValue(gk, out var repGk) ? repGk : gk)
          .Where(gk => gk > 0 && gk != newGn) // GN과 GM이 같으면 안됨
          .Distinct()
          .ToList();

      // ★ [추가] 해당 RBE가 UBOLT인지 확인하는 로직
      bool isUbolt = info.ExtraData != null &&
                     info.ExtraData.TryGetValue("Type", out string? typeStr) &&
                     typeStr == "UBOLT";

      // UBOLT는 나중에 연결하기 위해 종속 노드가 0개일 수 있으므로 삭제 면제
      if (dropIfEmpty && remappedDeps.Count == 0 && !isUbolt)
      {
        _rigids.Remove(rigidId);
        return false;
      }

      var extraCopy = info.ExtraData.ToDictionary(k => k.Key, v => v.Value);
      _rigids[rigidId] = new RigidInfo(newGn, remappedDeps, info.Cm, extraCopy);
      return true;
    }

    /// <summary>
    /// 모든 강체 요소에 대해 노드 ID 치환을 일괄 수행합니다.
    /// </summary>
    public void RemapAllNodes(IReadOnlyDictionary<int, int> oldToRep, bool dropIfEmpty = true)
    {
      var ids = _rigids.Keys.ToList();
      foreach (var rid in ids)
      {
        RemapNodes(rid, oldToRep, dropIfEmpty);
      }
    }

    /// <summary>
    /// 일반 부재(Elements) ID와 겹치지 않도록 강체 시작 ID를 동기화합니다.
    /// </summary>
    public void SynchronizeIDWithElements(Elements elements)
    {
      if (elements != null && elements.Count > 0)
      {
        int maxElementID = elements.Keys.Max();
        _nextRigidID = Math.Max(_nextRigidID, maxElementID + 1);
      }
    }

    /// <summary>
    /// 지정한 ID로 강체 요소를 강제로 추가합니다.
    /// </summary>
    public void AddWithID(int id, int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456", Dictionary<string, string>? extraData = null)
    {
      _rigids[id] = new RigidInfo(independentNodeID, dependentNodeIDs, cm, extraData);
      if (id >= _nextRigidID) _nextRigidID = id + 1;
      if (id > LastRigidID) LastRigidID = id;
    }

    public bool AppendDependentNodes(int rigidId, IEnumerable<int> additionalDependentNodes)
    {
      if (!_rigids.TryGetValue(rigidId, out var info))
        return false;

      if (additionalDependentNodes == null || !additionalDependentNodes.Any())
        return true; // 추가할 게 없으면 정상으로 간주하고 패스

      // 1. 기존 노드와 새 노드를 병합 후 정규화 (복 제거 및 정렬)
      var combinedNodes = info.DependentNodeIDs
          .Concat(additionalDependentNodes)
          .Distinct()
          .OrderBy(id => id)
          .ToList();

      // 2. 기존 ExtraData 깊은 복사
      var extraCopy = info.ExtraData.ToDictionary(k => k.Key, v => v.Value);

      // 3. 동일한 ID의 딕셔너리 항목을 새로운 RigidInfo 객체로 덮어쓰기 (불변성 유지)
      _rigids[rigidId] = new RigidInfo(info.IndependentNodeID, combinedNodes, info.Cm, extraCopy);

      return true;
    }

    public void Remove(int id) => _rigids.Remove(id);
    public bool Contains(int id) => _rigids.ContainsKey(id);

    public IEnumerator<KeyValuePair<int, RigidInfo>> GetEnumerator() => _rigids.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
  }
}
