using System;

namespace HiTessModelBuilder.Pipeline.Utils
{
  /// <summary>
  /// 3D 기하학 연산을 지원하는 공통 유틸리티 클래스입니다.
  /// </summary>
  public static class GeometryUtils
  {
    /// <summary>
    /// 시작점과 종료점을 기반으로 부재의 방향 벡터에 직교하는 Orientation 벡터(Local Y축)를 계산합니다.
    /// 기준 벡터(Global Z)를 부재 방향에 사영(Projection)하여 정확한 직교 벡터를 도출합니다.
    /// </summary>
    public static double[] CalculateBarOrientation(double[] startPos, double[] endPos)
    {
      if (startPos == null || startPos.Length < 3 || endPos == null || endPos.Length < 3)
        return new double[] { 0.0, 0.0, 1.0 };

      // 1. 부재의 방향 벡터 (검지 손가락, Local X)
      double dx = endPos[0] - startPos[0];
      double dy = endPos[1] - startPos[1];
      double dz = endPos[2] - startPos[2];

      double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
      if (length < 1e-12)
        return new double[] { 0.0, 0.0, 1.0 };

      double nx = dx / length;
      double ny = dy / length;
      double nz = dz / length;

      // 2. 임의의 보조 기준 벡터 (기본값 Global Z)
      double refX = 0.0, refY = 0.0, refZ = 1.0;

      // 부재가 Z축과 완벽히 평행하게 서 있다면, 기준 벡터를 Y축으로 변경
      if (Math.Abs(nz) > 0.99)
      {
        refX = 0.0; refY = 1.0; refZ = 0.0;
      }

      // 3. 직교화 (Vector Projection)
      // 기준 벡터에서 '방향 벡터와 평행한 성분'을 빼주면 완벽한 수직(엄지) 벡터가 나옵니다.
      double dotProduct = (refX * nx) + (refY * ny) + (refZ * nz);

      double thumbX = refX - (dotProduct * nx);
      double thumbY = refY - (dotProduct * ny);
      double thumbZ = refZ - (dotProduct * nz);

      // 4. 정규화
      double thumbLength = Math.Sqrt(thumbX * thumbX + thumbY * thumbY + thumbZ * thumbZ);
      if (thumbLength > 1e-12)
      {
        thumbX /= thumbLength;
        thumbY /= thumbLength;
        thumbZ /= thumbLength;
      }
      else
      {
        // 극단적인 경우를 대비한 방어 코드
        thumbX = 0.0; thumbY = 0.0; thumbZ = 1.0;
      }

      return new double[] { thumbX, thumbY, thumbZ };
    }
  }
}
