using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Model.Entities
{
  public sealed class FeModelContext
  {

    public Materials Materials { get; }
    public Properties Properties { get; }
    public Nodes Nodes { get; }
    public Elements Elements { get; }
    public Rigids Rigids { get; } = new Rigids();
    public PointMasses PointMasses { get; } = new PointMasses();
    public HashSet<int> WeldNodes { get; } = new HashSet<int>();

    /// <summary>
    /// FE 모델 전체 컨텍스트
    /// - 순수 데이터(Entity)들을 묶는 루트 객체
    /// - Service / Modifier의 공통 접근점
    /// </summary>
    public FeModelContext(
      Materials materials,
      Properties properties,
      Nodes nodes,
      Elements elements,
      Rigids rigids)
    {
      Materials = materials ?? throw new ArgumentNullException(nameof(materials));
      Properties = properties ?? throw new ArgumentNullException(nameof(properties));
      Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
      Elements = elements ?? throw new ArgumentNullException(nameof(elements));
      Rigids = rigids ?? throw new ArgumentNullException(nameof(rigids));
    }

    /// <summary>
    /// 빈 FE 모델 컨텍스트 생성
    /// </summary>
    public static FeModelContext CreateEmpty()
    {
      return new FeModelContext(
        new Materials(),
        new Properties(),
        new Nodes(),
        new Elements(),
        new Rigids()
      );
    }

    // [신규 추가] 노드가 병합(Collapse)될 때 용접점 ID도 갈아끼워주는 헬퍼 메써드
    public void RemapWeldNodes(IReadOnlyDictionary<int, int> oldToRep)
    {
      var oldNodes = WeldNodes.ToList();
      foreach (var oldNode in oldNodes)
      {
        if (oldToRep.TryGetValue(oldNode, out int newNode))
        {
          WeldNodes.Remove(oldNode);
          WeldNodes.Add(newNode); // 기존 용접점이 삭제되면 흡수된 새 노드에 용접 속성 이관
        }
      }
    }
  }
}
