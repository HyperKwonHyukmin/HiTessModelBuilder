using Cmb.Core.Geometry;
using FluentAssertions;

namespace Cmb.Core.Tests.Geometry;

// CSV ori = 단면 폭 방향(요소 z축). CBEAM v = ori × x̂.
public class BeamOrientationTests
{
    [Fact]
    public void HorizontalBeam_HorizontalOri_GivesVerticalWeb()
    {
        // X 방향 H 보, ori=+Y(플랜지 폭 방향) → v 는 수직(웹 수직) — 기존 기본 규칙(v=Z)과 같은 평면
        var v = BeamOrientation.FromSectionWidthDirection(new Vector3(1000, 0, 0), Vector3.UnitY);

        v.Should().NotBeNull();
        Math.Abs(v!.Value.Z).Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void VerticalColumn_OriY_PutsWebAlongX()
    {
        // 수직 기둥, ori=+Y → 플랜지가 Y 방향, 웹(v)은 X 방향. 기존 규칙(v=Y)이면 90° 돌아가 있었다.
        var v = BeamOrientation.FromSectionWidthDirection(new Vector3(0, 0, 1000), Vector3.UnitY);

        v.Should().NotBeNull();
        Math.Abs(v!.Value.X).Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void OriParallelToAxis_ReturnsNull()
    {
        BeamOrientation.FromSectionWidthDirection(new Vector3(0, 0, 1000), new Vector3(0, 0.1, 1)).Should().BeNull();
        BeamOrientation.FromSectionWidthDirection(new Vector3(0, 0, 1000), Vector3.Zero).Should().BeNull();
    }

    [Fact]
    public void Resolve_DefaultUnitZ_MatchesLegacyRuleExactly()
    {
        // 배관·구 JSON 은 UnitZ 를 갖는다 — 기존 규칙과 비트 단위로 같아야 BDF 가 안 바뀐다.
        var a = new Point3(0, 0, 0);
        foreach (var b in new[] { new Point3(1000, 0, 0), new Point3(300, 200, 700), new Point3(0, 0, 500), new Point3(10, 0, 500) })
            BeamOrientation.Resolve(a, b, Vector3.UnitZ).Should().Be(BeamOrientation.CalculateBarOrientation(a, b));
    }

    [Fact]
    public void Resolve_KeepsStoredOrientation_WhenNotParallel()
    {
        var v = BeamOrientation.Resolve(new Point3(0, 0, 0), new Point3(0, 0, 1000), Vector3.UnitX);
        v.Should().Be(Vector3.UnitX);
    }

    [Fact]
    public void Resolve_StoredParallelToAxis_FallsBackToLegacyRule()
    {
        // 형상 수정으로 축이 바뀌어 저장 v 가 축과 평행해져도 Nastran 에 평행 v 가 나가지 않는다.
        var a = new Point3(0, 0, 0); var b = new Point3(0, 0, 1000);
        BeamOrientation.Resolve(a, b, Vector3.UnitZ).Should().Be(BeamOrientation.CalculateBarOrientation(a, b));
    }
}
