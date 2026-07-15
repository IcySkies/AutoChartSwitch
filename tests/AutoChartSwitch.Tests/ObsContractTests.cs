using AutoChartSwitch.Core;
using AutoChartSwitch.Obs;

namespace AutoChartSwitch.Tests;

public sealed class ObsContractTests
{
    [Fact]
    public void AllNineUniqueCompatibleMappingsValidate()
    {
        var mappings = Mappings().AsDictionary();
        var inputs = new List<ObsInputInfo>
        {
            Text("title"), Text("artist"), FreeType("credits"), Text("difficulty-name"), Text("difficulty-number"),
            Image("jacket"), Image("difficulty-image"), Media("stat"), Media("showcase")
        };

        Assert.Null(ObsChartPublisher.ValidateMappings(mappings, inputs));
    }

    [Fact]
    public void CreditsMustMapToFreeTypeAndMappingsMustBeUnique()
    {
        var mappings = Mappings();
        var inputs = new List<ObsInputInfo>
        {
            Text("title"), Text("artist"), Text("credits"), Text("difficulty-name"), Text("difficulty-number"),
            Image("jacket"), Image("difficulty-image"), Media("stat"), Media("showcase")
        };
        Assert.Contains("incompatible", ObsChartPublisher.ValidateMappings(mappings.AsDictionary(), inputs), StringComparison.OrdinalIgnoreCase);

        mappings.Artist = "title";
        Assert.Contains("assigned more than once", ObsChartPublisher.ValidateMappings(mappings.AsDictionary(), inputs), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectionAndSettingKeysMatchObsContract()
    {
        var chart = FormatterAndValidationTests.ValidChart() with { Illustrator = "Ivy", Charter = "Casey", DifficultyNumber = 5m };
        var projection = ObsChartPublisher.Project(chart, "C:\\difficulty\\Master.png");

        Assert.Equal("Illust: Ivy\nChart: Casey", projection[ObsOutput.Credits]);
        Assert.Equal("5", projection[ObsOutput.DifficultyNumber]);
        Assert.Equal("text", ObsChartPublisher.GetSettingKey(ObsOutput.Title));
        Assert.Equal("file", ObsChartPublisher.GetSettingKey(ObsOutput.Jacket));
        Assert.Equal("local_file", ObsChartPublisher.GetSettingKey(ObsOutput.ShowcaseVideo));
    }

    private static ObsSourceMappings Mappings() => new()
    {
        Title = "title", Artist = "artist", Credits = "credits", DifficultyName = "difficulty-name",
        DifficultyNumber = "difficulty-number", Jacket = "jacket", DifficultyImage = "difficulty-image",
        StatMedia = "stat", ShowcaseVideo = "showcase"
    };

    private static ObsInputInfo Text(string name) => new(name, "text_gdiplus_v3", "text_gdiplus", ObsInputCategory.Text);
    private static ObsInputInfo FreeType(string name) => new(name, "text_ft2_source_v2", "text_ft2_source", ObsInputCategory.FreeTypeText);
    private static ObsInputInfo Image(string name) => new(name, "image_source", "image_source", ObsInputCategory.Image);
    private static ObsInputInfo Media(string name) => new(name, "ffmpeg_source", "ffmpeg_source", ObsInputCategory.Media);
}
