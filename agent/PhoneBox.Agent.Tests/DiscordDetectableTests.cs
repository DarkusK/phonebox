using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Discord's list of detectable games: what is kept from it, and how a running app's executable is matched.</summary>
public sealed class DiscordDetectableTests
{
    private static HashSet<string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return DiscordDetectable.Parse(doc);
    }

    [Fact]
    public void Only_windows_executables_that_are_not_launchers_are_kept()
    {
        var names = Parse("""
        [
          {"name":"Overwatch","executables":[{"os":"win32","name":"Overwatch.exe","is_launcher":false},{"os":"darwin","name":"Overwatch.app"}]},
          {"name":"WoW","executables":[{"os":"win32","name":"_retail_\\Wow.exe"},{"os":"win32","name":"Battle.net Launcher.exe","is_launcher":true}]},
          {"name":"Odd","executables":[{"os":"win32","name":">odd.exe"}]},
          {"name":"No executables"}
        ]
        """);
        Assert.Equal(["_retail_/wow.exe", "odd.exe", "overwatch.exe"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Something_other_than_a_list_keeps_nothing() => Assert.Empty(Parse("""{"message":"rate limited"}"""));

    [Theory]
    [InlineData(@"C:\Games\Overwatch\Overwatch.exe", true)]
    [InlineData(@"D:\Battle.net\World of Warcraft\_retail_\Wow.exe", true)]
    [InlineData(@"D:\Other\_classic_\Wow.exe", false)]           // the folder named in the list must be there
    [InlineData(@"C:\Program Files\Google\Chrome\chrome.exe", false)]
    [InlineData(@"C:\Games\notoverwatch.exe", false)]            // a whole file name, not the end of one
    [InlineData("overwatch.exe", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void An_executable_matches_by_the_end_of_its_path(string? path, bool expected)
    {
        var names = new HashSet<string>(["overwatch.exe", "_retail_/wow.exe"], StringComparer.Ordinal);
        Assert.Equal(expected, DiscordDetectable.Matches(names, path));
    }
}
