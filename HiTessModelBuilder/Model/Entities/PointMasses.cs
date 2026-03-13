using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 단일 점 질량(Point Mass, 예: CONM2) 데이터를 표현하는 불변(Immutable) 클래스입니다.
  /// </summary>
  public sealed class PointMass
  {
    public int NodeID { get; }
    public double Mass { get; }
    public IReadOnlyDictionary<string, string> ExtraData { get; }

    public PointMass(int nodeID, double mass, Dictionary<string, string>? extraData = null)
    {
      NodeID = nodeID;
      Mass = mass;
      ExtraData = extraData != null
        ? new Dictionary<string, string>(extraData)
        : new Dictionary<string, string>();
    }

    public override string ToString()
    {
      string extraInfo = ExtraData.Count > 0
        ? string.Join(", ", ExtraData.Select(kv => $"{kv.Key}:{kv.Value}"))
        : "None";
      return $"NodeID:{NodeID}, Mass:{Mass:F3}, ExtraData:{{{extraInfo}}}";
    }
  }

  /// <summary>
  /// 모델 내의 모든 점 질량(Point Mass) 요소를 관리하는 컬렉션 클래스입니다.
  /// </summary>
  public class PointMasses : IEnumerable<KeyValuePair<int, PointMass>>
  {
    private readonly Dictionary<int, PointMass> _pointMasses = new();

    // 일반 Element나 Rigid ID와 겹치지 않도록 필요시 동기화할 수 있는 기준 ID
    private int _nextPointMassID = 8000001;
    public int LastPointMassID { get; private set; } = 0;

    public PointMass this[int id]
    {
      get
      {
        if (!_pointMasses.TryGetValue(id, out var pm))
          throw new KeyNotFoundException($"Point Mass ID {id} does not exist.");
        return pm;
      }
    }

    public int Count => _pointMasses.Count;
    public IEnumerable<int> Keys => _pointMasses.Keys;

    /// <summary>
    /// 새로운 Point Mass를 자동 ID로 추가하고 생성된 ID를 반환합니다.
    /// </summary>
    public int AddNew(int nodeID, double mass, Dictionary<string, string>? extraData = null)
    {
      int newID = _nextPointMassID++;
      _pointMasses[newID] = new PointMass(nodeID, mass, extraData);
      LastPointMassID = newID;
      return newID;
    }

    /// <summary>
    /// 지정한 ID로 Point Mass를 강제 추가합니다. (복원/명시적 생성용)
    /// </summary>
    public void AddWithID(int id, int nodeID, double mass, Dictionary<string, string>? extraData = null)
    {
      _pointMasses[id] = new PointMass(nodeID, mass, extraData);
      if (id >= _nextPointMassID) _nextPointMassID = id + 1;
      if (id > LastPointMassID) LastPointMassID = id;
    }

    /// <summary>
    /// 일반 부재(Elements)나 RBE ID와 겹치지 않도록 시작 ID를 동기화합니다.
    /// </summary>
    public void SynchronizeID(int maxExistingID)
    {
      _nextPointMassID = Math.Max(_nextPointMassID, maxExistingID + 1);
    }

    /// <summary>
    /// 노드 병합 결과를 반영하여 Point Mass가 매달려 있는 노드 ID를 일괄 갱신합니다.
    /// </summary>
    public void RemapAllNodes(IReadOnlyDictionary<int, int> oldToRep)
    {
      var ids = _pointMasses.Keys.ToList();
      foreach (var pid in ids)
      {
        var pm = _pointMasses[pid];

        // 해당 Point Mass의 노드가 병합(삭제) 대상이라면
        if (oldToRep.TryGetValue(pm.NodeID, out int newId))
        {
          var extraCopy = pm.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
          // 불변 객체이므로 동일 ID에 새로운 정보로 덮어쓰기
          _pointMasses[pid] = new PointMass(newId, pm.Mass, extraCopy);
        }
      }
    }

    public void Remove(int id) => _pointMasses.Remove(id);
    public bool Contains(int id) => _pointMasses.ContainsKey(id);

    public IEnumerator<KeyValuePair<int, PointMass>> GetEnumerator() => _pointMasses.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
  }
}