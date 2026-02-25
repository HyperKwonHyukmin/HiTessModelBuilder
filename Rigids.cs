using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Entities
{
  public sealed class RigidInfo
  {
    public int IndependentNodeID { get; }
    public IReadOnlyList<int> DependentNodeIDs { get; }
    public string Cm { get; }

    public RigidInfo(int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456")
    {
      IndependentNodeID = independentNodeID;
      // 리스트 복사 및 읽기 전용화 (불변성 보장)
      DependentNodeIDs = dependentNodeIDs.ToList().AsReadOnly();
      Cm = cm;
    }

    public override string ToString()
    {
      return $"Indep:{IndependentNodeID}, Dep:[{string.Join(",", DependentNodeIDs)}], CM:{Cm}";
    }
  }

  public class Rigids : IEnumerable<KeyValuePair<int, RigidInfo>>
  {
    private readonly Dictionary<int, RigidInfo> _rigids = new();

    // ★ Nastran에서 CBEAM과 Element ID 충돌을 막기 위해 9,000,001번부터 발급
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

    public int AddNew(int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456")
    {
      int newID = _nextRigidID++;
      _rigids[newID] = new RigidInfo(independentNodeID, dependentNodeIDs, cm);
      LastRigidID = newID;
      return newID;
    }

    public void AddWithID(int id, int independentNodeID, IEnumerable<int> dependentNodeIDs, string cm = "123456")
    {
      _rigids[id] = new RigidInfo(independentNodeID, dependentNodeIDs, cm);
      if (id >= _nextRigidID) _nextRigidID = id + 1;
      if (id > LastRigidID) LastRigidID = id;
    }

    public void Remove(int id) => _rigids.Remove(id);
    public bool Contains(int id) => _rigids.ContainsKey(id);
    public int Count => _rigids.Count;

    public IEnumerator<KeyValuePair<int, RigidInfo>> GetEnumerator() => _rigids.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
  }
}
