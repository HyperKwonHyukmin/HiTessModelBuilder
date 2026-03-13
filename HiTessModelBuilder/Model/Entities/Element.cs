using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 순수 Element 1개가 가지는 데이터 클래스
  /// </summary>
  public sealed class Element
  {
    public IReadOnlyList<int> NodeIDs { get; }
    public int PropertyID { get; }
    public IReadOnlyList<double> Orientation { get; }
    public IReadOnlyDictionary<string, string> ExtraData { get; }

    public Element(
       IEnumerable<int> nodeIDs,
       int propertyID,
       IEnumerable<double>? orientation = null, // <- 이 부분이 반드시 추가되어야 합니다.
       Dictionary<string, string>? extraData = null)
    {
      if (nodeIDs == null)
        throw new ArgumentNullException(nameof(nodeIDs));

      var list = nodeIDs.ToList();
      if (list.Count < 2)
        throw new ArgumentException("Element must have at least two nodes.");

      if (list.Distinct().Count() != list.Count)
        throw new ArgumentException("Element nodeIDs must be unique.");

      NodeIDs = list.AsReadOnly();
      PropertyID = propertyID;

      // 방향 벡터 설정 (입력값이 없거나 길이가 3이 아니면 기본 Z축 설정)
      var oriList = orientation?.ToList();
      if (oriList != null && oriList.Count == 3)
      {
        Orientation = oriList.AsReadOnly();
      }
      else
      {
        Orientation = new List<double> { 0.0, 0.0, 1.0 }.AsReadOnly();
      }

      ExtraData = extraData != null
        ? new Dictionary<string, string>(extraData)
        : new Dictionary<string, string>();
    }

    public override string ToString()
    {
      string nodesPart = $"Nodes:[{string.Join(",", NodeIDs)}]";
      string propPart = $"PropertyID:{PropertyID}";
      string oriPart = $"Orientation:({Orientation[0]:F2}, {Orientation[1]:F2}, {Orientation[2]:F2})";

      string extraPart;
      if (ExtraData == null || ExtraData.Count == 0)
      {
        extraPart = "ExtraData:{}";
      }
      else
      {
        // 보기 좋게 key 정렬 + key=value로 출력
        var pairs = ExtraData
          .OrderBy(kv => kv.Key)
          .Select(kv =>
          {
            string k = kv.Key ?? "";
            string v = kv.Value ?? "";
            return $"{k}={v}";
          });

        extraPart = $"ExtraData:{{{string.Join(", ", pairs)}}}";
      }

      return $"{nodesPart}, {propPart}, {oriPart}, {extraPart}";
    }

  }

  public class Elements : IEnumerable<KeyValuePair<int, Element>>
  {
    private readonly Dictionary<int, Element> _elements = new();

    private int _nextElementID = 1;
    public int LastElementID { get; private set; } = 0;

    public Elements() { }

    public Element this[int id]
    {
      get
      {
        if (!_elements.TryGetValue(id, out var element))
          throw new KeyNotFoundException($"Element ID {id} does not exist.");
        return element;
      }
    }

    /// <summary>
    /// 새로운 Element 추가 : ID는 자동 생성
    /// </summary>
    public int AddNew(
          List<int> nodeIDs,
          int propertyID,
          IEnumerable<double>? orientation = null,
          Dictionary<string, string>? extraData = null)
    {
      int newID = _nextElementID++;

      var element = new Element(nodeIDs, propertyID, orientation, extraData);
      _elements[newID] = element;

      LastElementID = newID;
      return newID;
    }

    /// <summary>
    /// Element 추가 시, 특정 ID로 추가
    /// </summary>
    public void AddWithID(
          int elementID,
          List<int> nodeIDs,
          int propertyID,
          IEnumerable<double>? orientation = null,
          Dictionary<string, string>? extraData = null)
    {
      var element = new Element(nodeIDs, propertyID, orientation, extraData);
      _elements[elementID] = element;

      if (elementID >= _nextElementID)
        _nextElementID = elementID + 1;

      if (elementID > LastElementID)
        LastElementID = elementID;
    }

    /// <summary>
    /// Element 제거 
    /// </summary>
    public void Remove(int elementID)
    {
      _elements.Remove(elementID);

      if (elementID == LastElementID)
      {
        if (_elements.Count > 0)
        {
          LastElementID = _elements.Keys.Max();
          _nextElementID = LastElementID + 1;
        }
        else
        {
          LastElementID = 0;
          _nextElementID = 1;
        }
      }
    }

    /// <summary>
    /// 특정 elementID가 존재하는지 확인
    /// </summary>
    public bool Contains(int elementID)
      => _elements.ContainsKey(elementID);

    public IEnumerable<int> Keys
      => _elements.Keys;

    /// <summary>
    /// Element 갯수 반환
    /// </summary>
    public int Count
      => _elements.Count;

    /// <summary>
    /// 특정 Node가 Element 생성에 몇번 사용되었는가
    /// </summary>
    public int CountNodeUsage(int nodeID)
    {
      int count = 0;
      foreach (var element in _elements.Values)
        if (element.NodeIDs.Contains(nodeID))
          count++;
      return count;
    }

    // Elements 클래스 내부에 추가
    public bool TryGetValue(int id, out Element element)
    {
      return _elements.TryGetValue(id, out element);
    }

    /// <summary>
    /// 모든 Node가 Element 사용에 몇번 사용되었는지 딕셔너리로 반환
    /// </summary>
    public Dictionary<int, int> CountAllNodeUsages()
    {
      var dict = new Dictionary<int, int>();

      foreach (var element in _elements.Values)
      {
        foreach (int node in element.NodeIDs)
        {
          if (dict.ContainsKey(node)) dict[node]++;
          else dict[node] = 1;
        }
      }
      return dict;
    }


    public IReadOnlyDictionary<int, Element> AsReadOnly()
      => _elements;


    public IEnumerator<KeyValuePair<int, Element>> GetEnumerator()
      => _elements.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
      => GetEnumerator();

  }
}

