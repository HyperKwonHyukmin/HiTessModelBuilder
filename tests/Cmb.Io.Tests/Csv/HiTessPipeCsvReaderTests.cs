using Cmb.Io.Csv;
using FluentAssertions;

namespace Cmb.Io.Tests.Csv;

public class HiTessPipeCsvReaderTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pipe.csv");

    [Fact]
    public void Read_FixtureFile_ParsesAllRows()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, skips) = reader.Read(FixturePath);

        rows.Count.Should().Be(8);
        skips.Should().BeEmpty();
    }

    [Fact]
    public void Read_TubiRow_CoordinatesAndDimensions()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var tubi = rows[0];
        tubi.Type.Should().Be("TUBI");
        tubi.APos.Should().BeEquivalentTo([76600.0, 8965.0, 37252.0]);
        tubi.LPos.Should().BeEquivalentTo([77100.0, 8965.0, 37252.0]);
        tubi.OutDia.Should().BeApproximately(73.0, 1e-10);
        tubi.Thick.Should().BeApproximately(3.05, 1e-10);
        tubi.InterPos.Should().BeNull();
    }

    [Fact]
    public void Read_ElboRow_InterPosTwoPoints()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var elbo = rows[1];
        elbo.Type.Should().Be("ELBO");
        elbo.InterPos.Should().NotBeNull();
        elbo.InterPos!.Length.Should().Be(6);
        elbo.InterPos[0].Should().BeApproximately(78703.0, 1e-6);
        elbo.InterPos[3].Should().BeApproximately(78737.0, 1e-6);
    }

    [Fact]
    public void Read_UboltRow_RestAndMass()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var ubolt = rows[2];
        ubolt.Type.Should().Be("UBOLT");
        ubolt.Rest.Should().Be("3");
        ubolt.Mass.Should().BeApproximately(3.0968, 1e-4);
        ubolt.OutDia.Should().Be(0.0);
    }

    [Fact]
    public void Read_ValvRow_ZeroDiameterWithMass()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var valv = rows[3];
        valv.Type.Should().Be("VALV");
        valv.OutDia.Should().Be(0.0);
        valv.Thick.Should().Be(0.0);
        valv.Mass.Should().BeApproximately(14.3, 1e-10);
        valv.Normal.Should().BeEquivalentTo([0.0, 0.0, 0.0]);
    }

    [Fact]
    public void Read_FlanRow_BeamWithMass()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var flan = rows[4];
        flan.Type.Should().Be("FLAN");
        flan.OutDia.Should().BeApproximately(48.3, 1e-10);
        flan.Thick.Should().BeApproximately(3.68, 1e-10);
        flan.Mass.Should().BeApproximately(1.72, 1e-10);
    }

    [Fact]
    public void Read_TeeRow_P3PosPresent()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var tee = rows[5];
        tee.Type.Should().Be("TEE");
        tee.P3Pos.Should().NotBeNull();
        tee.P3Pos!.Length.Should().Be(3);
        tee.P3Pos[0].Should().BeApproximately(90351.0, 1e-6);
        tee.P3Pos[2].Should().BeApproximately(37319.0, 1e-6);
        tee.OutDia2.Should().BeApproximately(48.3, 1e-10);
        tee.Thick2.Should().BeApproximately(2.77, 1e-10);
    }

    [Fact]
    public void Read_ReduRow_ReducingDimensions()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var redu = rows[6];
        redu.Type.Should().Be("REDU");
        redu.OutDia.Should().BeApproximately(660.0, 1e-10);
        redu.Thick.Should().BeApproximately(6.35, 1e-10);
        redu.OutDia2.Should().BeApproximately(457.0, 1e-10);
        redu.Thick2.Should().BeApproximately(4.78, 1e-10);
    }

    [Fact]
    public void Read_AttaRow_MassOnly()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var atta = rows[7];
        atta.Type.Should().Be("ATTA");
        atta.Mass.Should().BeApproximately(1.3478, 1e-4);
        atta.OutDia.Should().Be(0.0);
    }

    [Fact]
    public void Read_NullNormal_DefaultsToZeroVector()
    {
        var reader = new HiTessPipeCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var valv = rows[3];
        valv.Normal.Length.Should().Be(3);
        valv.Normal[0].Should().Be(0.0);
    }
}
