using Avalonia.Styling;
using JsonToolbox.App;

namespace JsonToolbox.App.Tests;

public class ThemeModesTests
{
    [Theory]
    [InlineData(null, "System")]
    [InlineData("", "System")]
    [InlineData("Default", "System")]
    [InlineData("System", "System")]
    [InlineData("Light", "Light")]
    [InlineData("dark", "Dark")]
    public void Normalize_maps_stored_values_to_three_modes(string? stored, string expected)
        => Assert.Equal(expected, ThemeModes.Normalize(stored));

    [Fact]
    public void Next_cycles_system_light_dark()
    {
        Assert.Equal(ThemeModes.Light, ThemeModes.Next(null));
        Assert.Equal(ThemeModes.Dark, ThemeModes.Next(ThemeModes.Light));
        Assert.Equal(ThemeModes.System, ThemeModes.Next(ThemeModes.Dark));
    }

    [Fact]
    public void System_follows_the_os()
    {
        Assert.Equal(ThemeVariant.Default, ThemeModes.ToVariant(null));
        Assert.Equal(ThemeVariant.Light, ThemeModes.ToVariant("Light"));
        Assert.Equal(ThemeVariant.Dark, ThemeModes.ToVariant("Dark"));
    }
}
