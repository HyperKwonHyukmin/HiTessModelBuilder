using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// ���� Element 1���� ������ ������ Ŭ����
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
       IEnumerable<double>? orientation = null, // <- �� �κ��� �ݵ�� �߰��Ǿ�� �մϴ�.
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

      // ���� ���� ���� (�Է°��� ���ų� ���̰� 3�� �ƴϸ� �⺻ Z�� ����)
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
        // ���� ���� key ���� + key=value�� ���
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
    /// ���ο� Element �߰� : ID�� �ڵ� ����
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
    /// Element �߰� ��, Ư�� ID�� �߰�
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
    /// Element ���� 
    /// </summary>
    public void Remove(int elementID)
    {
      _elements.Remove(elementID);
      // LastElementID/_nextElementID는 단조 증가 방식으로 관리 — 삭제 시 O(N) Keys.Max() 재계산 불필요
      // FE 모델에서 ID 공백은 정상이며 BDF 출력에도 영향 없음
    }

    /// <summary>
    /// Ư�� elementID�� �����ϴ��� Ȯ��
    /// </summary>
    public bool Contains(int elementID)
      => _elements.ContainsKey(elementID);

    public IEnumerable<int> Keys
      => _elements.Keys;

    /// <summary>
    /// Element ���� ��ȯ
    /// </summary>
    public int Count
      => _elements.Count;

    /// <summary>
    /// Ư�� Node�� Element ������ ��� ���Ǿ��°�
    /// </summary>
    public int CountNodeUsage(int nodeID)
    {
      int count = 0;
      foreach (var element in _elements.Values)
        if (element.NodeIDs.Contains(nodeID))
          count++;
      return count;
    }

    // Elements Ŭ���� ���ο� �߰�
    public bool TryGetValue(int id, out Element element)
    {
      return _elements.TryGetValue(id, out element);
    }

    /// <summary>
    /// ��� Node�� Element ��뿡 ��� ���Ǿ����� ��ųʸ��� ��ȯ
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

