using Cmb.Io.Csv;
using FluentAssertions;

namespace Cmb.Io.Tests.Csv;

public class HiTessEquipCsvReaderTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "equip.csv");

    [Fact]
    public void Read_FixtureFile_ParsesAllRows()
    {
        var reader = new HiTessEquipCsvReader();
        var (rows, skips) = reader.Read(FixturePath);

        rows.Count.Should().Be(2);
        skips.Should().BeEmpty();
    }

    [Fact]
    public void Read_FirstRow_PositionAndMass()
    {
        var reader = new HiTessEquipCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        var equip = rows[0];
        equip.Pos[0].Should().BeApproximately(112065.0, 1e-6);
        equip.Pos[1].Should().BeApproximately(-7685.0, 1e-6);
        equip.Pos[2].Should().BeApproximately(30988.0, 1e-6);
        equip.Cog[0].Should().BeApproximately(112100.0, 1e-6);
        equip.InterPos.Should().BeNull();
        equip.Mass.Should().BeApproximately(1.0, 1e-10);
        equip.Wvol.Should().Be(0.0);
    }

    [Fact]
    public void Read_SecondRow_ZeroMass()
    {
        var reader = new HiTessEquipCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        rows[1].Mass.Should().Be(0.0);
    }

    [Fact]
    public void Read_NegativeYCoordinate_ParsedCorrectly()
    {
        var reader = new HiTessEquipCsvReader();
        var (rows, _) = reader.Read(FixturePath);

        rows[0].Pos[1].Should().BeNegative();
    }

    [Fact]
    public void Read_InterPos_ParsedWhenPresent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "name,pos,cog,interPos,mass,wvol" + Environment.NewLine +
                "EQ,X 0mm Y 0mm Z 0mm,X 10mm Y 20mm Z 30mm,X 1mm Y 2mm Z 3mm+X 4mm Y 5mm Z 6mm,7,8");

            var reader = new HiTessEquipCsvReader();
            var (rows, skips) = reader.Read(path);

            skips.Should().BeEmpty();
            rows.Should().ContainSingle();
            rows[0].InterPos.Should().BeEquivalentTo(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }, o => o.WithStrictOrdering());
            rows[0].Wvol.Should().Be(8.0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
