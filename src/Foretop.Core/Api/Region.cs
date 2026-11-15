namespace Foretop.Core.Api;

public enum Region
{
    Eu,
    Na,
    Asia,
}

public static class Regions
{
    public static string ApiHost(Region r) => r switch
    {
        Region.Eu => "https://api.worldofwarships.eu",
        Region.Na => "https://api.worldofwarships.com",
        Region.Asia => "https://api.worldofwarships.asia",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    /// <summary>Official public player profile page.</summary>
    public static string ProfileUrl(Region r, long accountId, string nickname)
    {
        var host = r switch
        {
            Region.Eu => "https://profile.worldofwarships.eu",
            Region.Na => "https://profile.worldofwarships.com",
            Region.Asia => "https://profile.worldofwarships.asia",
            _ => throw new ArgumentOutOfRangeException(nameof(r)),
        };
        return $"{host}/statistics/{accountId}-{Uri.EscapeDataString(nickname)}";
    }

    /// <summary>Third-party profile on wows-numbers.com.</summary>
    public static string WowsNumbersUrl(Region r, long accountId, string nickname)
    {
        var sub = r switch { Region.Eu => "", Region.Na => "na.", Region.Asia => "asia.", _ => "" };
        return $"https://{sub}wows-numbers.com/player/{accountId},{Uri.EscapeDataString(nickname)}/";
    }

    public static string Label(Region r) => r switch
    {
        Region.Eu => "Europe",
        Region.Na => "North America",
        Region.Asia => "Asia",
        _ => r.ToString(),
    };
}
