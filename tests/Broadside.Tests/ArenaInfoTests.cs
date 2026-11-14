using System.Text;
using Broadside.Core.Game;

namespace Broadside.Tests;

public class ArenaInfoTests
{
    // Shape of a real tempArenaInfo.json (2016 client); nicknames replaced.
    private const string Sample = """
        {"clientVersionFromXml": "0, 5, 15, 216844", "gameMode": 7, "clientVersionFromExe": "0, 5, 15, 216844",
         "mapDisplayName": "42_Neighbors", "mapId": 26, "matchGroup": "pvp", "duration": 1200, "gameLogic": "Domination",
         "name": "12x12", "scenario": "Domination", "playerID": 0,
         "vehicles": [
           {"shipId": 4288559088, "relation": 1, "id": 537655300, "name": "Ally_One"},
           {"shipId": 3555669712, "relation": 2, "id": 843147, "name": "Enemy_One"},
           {"shipId": 4284463088, "relation": 2, "id": 537631244, "name": "Enemy_Two"},
           {"shipId": 4181604048, "relation": 0, "id": 500000001, "name": "Me"},
           {"shipId": 4181604049, "relation": 2, "id": -5, "name": ":Bot_Smith:"}
         ],
         "playersPerTeam": 12, "dateTime": "19.12.2016 17:47:18", "mapName": "spaces/42_Neighbors",
         "playerName": "Me", "scenarioConfigId": 184, "teamsCount": 2, "logic": "Domination", "playerVehicle": "PRSC107-Schors"}
        """;

    [Fact]
    public void Parses_roster_and_metadata()
    {
        Assert.True(ArenaInfoParser.TryParse(Sample, out var info, out var error), error);
        Assert.NotNull(info);
        Assert.Equal(BattleType.Random, info!.BattleType);
        Assert.Equal("Neighbors", info.MapName);
        Assert.Equal(12, info.PlayersPerTeam);
        Assert.Equal(new DateTime(2016, 12, 19, 17, 47, 18), info.StartedAt);
        Assert.Equal(5, info.Vehicles.Count);
        Assert.Equal(2, info.Friendly.Count());
        Assert.Equal(3, info.Enemy.Count());
        Assert.Equal("Me", info.Self!.Name);
        Assert.Equal(4288559088L, info.Vehicles[0].ShipId); // larger than int32
    }

    [Fact]
    public void Detects_bots_by_nickname_or_negative_id()
    {
        ArenaInfoParser.TryParse(Sample, out var info, out _);
        Assert.Single(info!.Vehicles, v => v.IsBot);
        Assert.True(new ArenaVehicle("Normal", 1, 2, -1).IsBot);
        Assert.True(new ArenaVehicle(":X:", 1, 2, 10).IsBot);
        Assert.False(new ArenaVehicle("Normal", 1, 2, 10).IsBot);
    }

    [Fact]
    public void Battle_key_is_stable_and_order_independent()
    {
        ArenaInfoParser.TryParse(Sample, out var a, out _);
        var reordered = Sample.Replace("\"Ally_One\"}", "\"Ally_One\"},{\"shipId\": 1, \"relation\": 1, \"id\": 1, \"name\": \"Extra\"}");
        ArenaInfoParser.TryParse(reordered, out var b, out _);
        ArenaInfoParser.TryParse(Sample, out var c, out _);
        Assert.Equal(a!.BattleKey, c!.BattleKey);
        Assert.NotEqual(a.BattleKey, b!.BattleKey);
        Assert.Equal(16, a.BattleKey.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"vehicles\": [")]
    [InlineData("{\"matchGroup\": \"pvp\"}")]
    [InlineData("{\"vehicles\": []}")]
    [InlineData("[1,2]")]
    public void Rejects_partial_or_empty_files(string json)
    {
        Assert.False(ArenaInfoParser.TryParse(json, out var info, out var error));
        Assert.Null(info);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("pvp", BattleType.Random)]
    [InlineData("ranked", BattleType.Ranked)]
    [InlineData("clan", BattleType.Clan)]
    [InlineData("pve", BattleType.Coop)]
    [InlineData("something_new", BattleType.Unknown)]
    public void Maps_match_groups(string group, BattleType expected) =>
        Assert.Equal(expected, BattleTypes.FromMatchGroup(group));

    [Theory]
    [InlineData("42_Neighbors", "Neighbors")]
    [InlineData("spaces/17_NA_fault_line", "NA fault line")]
    [InlineData(null, "Unknown map")]
    public void Prettifies_map_names(string? raw, string expected) =>
        Assert.Equal(expected, BattleTypes.PrettyMapName(raw));

    [Fact]
    public void Reads_replay_json_header()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = Encoding.UTF8.GetBytes(Sample);
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(new byte[] { 0x12, 0x32, 0x34, 0x11 });
                bw.Write(1u);
                bw.Write(json.Length);
                bw.Write(json);
                bw.Write(new byte[64]); // packet data would follow
            }
            var text = ReplayHeaderReader.ReadArenaJson(path);
            Assert.True(ArenaInfoParser.TryParse(text, out var info, out _));
            Assert.Equal(5, info!.Vehicles.Count);
        }
        finally
        {
            File.Delete(path); // test scratch file in %TEMP%
        }
    }

    [Fact]
    public void Rejects_non_replay_files()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a replay at all");
        Assert.Throws<InvalidDataException>(() => ReplayHeaderReader.ReadArenaJson(path));
    }
}
