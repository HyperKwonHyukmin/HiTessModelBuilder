using Cmb.Io.Csv;
using FluentAssertions;

namespace Cmb.Io.Tests.Csv;

public class HiTessStructureCsvReaderTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "structure.csv");

    [Fact]
    public void Read_FixtureFile_ParsesAllRows()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, skips) = reader.Read(FixturePath);

        rows.Count.Should().Be(7);
        skips.Should().BeEmpty();
    }

    [Fact]
    public void Read_FbarRow_SectionTypeAndDims()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var fbar = rows[0];
        fbar.SectionType.Should().Be("FBAR");
        fbar.Dims.Should().BeEquivalentTo([65.0, 16.0]);
        fbar.SizeRaw.Should().Be("FBAR_65x16");
    }

    [Fact]
    public void Read_FbarRow_Coordinates()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var fbar = rows[0];
        fbar.StartPos.Should().BeEquivalentTo([90072.0, 4908.0, 38610.0]);
        fbar.EndPos.Should().BeEquivalentTo([90072.0, 4908.0, 39616.0]);
        fbar.Ori.Should().BeEquivalentTo([1.0, 0.0, 0.0]);
    }

    [Fact]
    public void Read_FbarRow_NoWeld()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        rows[0].Weld.Should().BeEmpty();
    }

    [Fact]
    public void Read_RbarRow_SectionType()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        rows[1].SectionType.Should().Be("RBAR");
        rows[1].Dims.Should().BeEquivalentTo([19.0]);
    }

    [Fact]
    public void Read_TubeRow_SectionTypeAndDims()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var tube = rows[2];
        tube.SectionType.Should().Be("TUBE");
        tube.Dims.Should().BeEquivalentTo([42.7, 3.25]);
    }

    [Fact]
    public void Read_BulbRow_SectionType()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        rows[3].SectionType.Should().Be("BULB");
        rows[3].Dims.Should().BeEquivalentTo([65.0, 9.0]);
    }

    [Fact]
    public void Read_AngRow_WeldEnd()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var ang150 = rows[5];
        ang150.SectionType.Should().Be("ANG");
        ang150.Dims.Should().BeEquivalentTo([150.0, 150.0, 15.0]);
        ang150.Weld.Should().Be("end");
    }

    [Fact]
    public void Read_BeamRow_SectionTypeAndDims()
    {
        var reader = new HiTessStructureCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var beam = rows[6];
        beam.SectionType.Should().Be("BEAM");
        beam.Dims.Should().BeEquivalentTo([100.0, 100.0, 6.0, 8.0]);
    }

    [Fact]
    public void Read_InvalidLine_CollectedAsSkip()
    {
        var csv = "name,type,pos,poss,pose,size,stru,ori\nBAD_LINE,X Y Z";
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, csv);
            var reader = new HiTessStructureCsvReader();
            var (rows, skips) = reader.Read(tmpFile);

            rows.Should().BeEmpty();
            skips.Should().HaveCount(1);
            skips[0].Kind.Should().Be("Structure");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
