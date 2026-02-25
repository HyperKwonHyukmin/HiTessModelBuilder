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

    /// <summary>
    /// FE 모델 전체 컨텍스트
    /// - 순수 데이터(Entity)들을 묶는 루트 객체
    /// - Service / Modifier의 공통 접근점
    /// </summary>
    public FeModelContext(
      Materials materials,
      Properties properties,
      Nodes nodes,
      Elements elements)
    {
      Materials = materials ?? throw new ArgumentNullException(nameof(materials));
      Properties = properties ?? throw new ArgumentNullException(nameof(properties));
      Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
      Elements = elements ?? throw new ArgumentNullException(nameof(elements));
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
        new Elements()
      );
    }
  }
}
