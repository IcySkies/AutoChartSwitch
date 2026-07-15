using System.Text.Json.Serialization;

namespace AutoChartSwitch.Core;

public sealed record ChartInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Illustrator { get; init; } = "";
    public string Charter { get; init; } = "";
    public string DifficultyName { get; init; } = "";
    public decimal DifficultyNumber { get; init; }
    public string JacketPath { get; init; } = "";
    public string StatMediaPath { get; init; } = "";
    public string ShowcaseVideoPath { get; init; } = "";

    [JsonIgnore]
    public string CreditsText => ChartFormatter.FormatCredits(Illustrator, Charter);
}

public static class ChartFormatter
{
    public static string FormatDifficulty(decimal value)
    {
        if (value == 0m) return "";
        if (value < 10m && decimal.Truncate(value) == value)
            return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string FormatCredits(string illustrator, string charter) =>
        $"Illust: {illustrator}\nChart: {charter}";

    public static string GetDifficultyImagePath(string customPath, string difficultyName) =>
        Path.GetFullPath(Path.Combine(customPath, $"{difficultyName}.png"));
}
