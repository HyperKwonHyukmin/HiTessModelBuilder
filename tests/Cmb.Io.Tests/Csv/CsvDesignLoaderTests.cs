using Cmb.Io.Csv;
using FluentAssertions;

namespace Cmb.Io.Tests.Csv;

public class CsvDesignLoaderTests
{
    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void Load_ExplicitPaths_AggregatesAllCategories()
    {
        var loader = new CsvDesignLoader();
        var data = loader.Load(
            Path.Combine(FixturesDir, "structure.csv"),
            Path.Combine(FixturesDir, "pipe.csv"),
            Path.Combine(FixturesDir, "equip.csv")
        );

        data.Beams.Count.Should().Be(7);
        data.Pipes.Count.Should().Be(8);
        data.Equips.Count.Should().Be(2);
        data.Skips.Should().BeEmpty();
    }

    [Fact]
    public void Load_NullPaths_EmptyCollections()
    {
        var loader = new CsvDesignLoader();
        var data = loader.Load(null, null, null);

        data.Beams.Should().BeEmpty();
        data.Pipes.Should().BeEmpty();
        data.Equips.Should().BeEmpty();
    }

    [Fact]
    public void Load_MissingFile_SkipsGracefully()
    {
        var loader = new CsvDesignLoader();
        var data = loader.Load(
            @"C:\nonexistent\file.csv",
            Path.Combine(FixturesDir, "pipe.csv"),
            null
        );

        data.Beams.Should().BeEmpty();
        data.Pipes.Count.Should().Be(8);
    }
}
