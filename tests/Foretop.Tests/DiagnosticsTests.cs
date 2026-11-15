using Foretop.Core.Game;

namespace Foretop.Tests;

public class DiagnosticsTests
{
    private static string FakeInstall(bool withReplay, bool withArena)
    {
        var root = Path.Combine(Path.GetTempPath(), "foretop-tests", "wows-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "replays"));
        File.WriteAllText(Path.Combine(root, "game_info.xml"), "<protocol />");
        if (withReplay) File.WriteAllBytes(Path.Combine(root, "replays", "20261114_200000_PASB018-Iowa_19_OC_prey.wowsreplay"), [0x12, 0x32, 0x34, 0x11]);
        if (withArena)
            File.WriteAllText(Path.Combine(root, "replays", "tempArenaInfo.json"),
                """{"matchGroup":"ranked","mapDisplayName":"45_Zigzag","vehicles":[{"shipId":1,"relation":0,"id":1,"name":"Me"}]}""");
        return root;
    }

    [Fact]
    public void Healthy_install_without_a_running_game()
    {
        var checks = GameDiagnostics.Run(FakeInstall(withReplay: true, withArena: true), () => null, () => 0);
        Assert.Equal(CheckLevel.Ok, checks.Single(c => c.Name == "Game folder").Level);
        var replays = checks.Single(c => c.Name == "Replays");
        Assert.Equal(CheckLevel.Ok, replays.Level);
        Assert.Contains("1 replays", replays.Detail);
        // Game not running, so a present arena file is a leftover, not a live battle.
        Assert.Contains("Left over", checks.Single(c => c.Name == "Battle file").Detail);
    }

    [Fact]
    public void Missing_replays_are_flagged()
    {
        var checks = GameDiagnostics.Run(FakeInstall(withReplay: false, withArena: false), () => null, () => 0);
        Assert.Equal(CheckLevel.Warning, checks.Single(c => c.Name == "Replays").Level);
        Assert.Equal(CheckLevel.Info, checks.Single(c => c.Name == "Battle file").Level);
    }

    [Fact]
    public void Running_game_makes_a_fresh_arena_file_the_current_battle()
    {
        var root = FakeInstall(withReplay: true, withArena: true);
        var game = new GameProcessInfo(Path.Combine(root, "bin", "1", "bin64", "WorldOfWarships64.exe"), root, DateTime.Now.AddMinutes(-5));
        var checks = GameDiagnostics.Run(null, () => game, () => 1);
        Assert.Equal(CheckLevel.Ok, checks.Single(c => c.Name == "Game running").Level);
        Assert.Contains(root, checks.Single(c => c.Name == "Game folder").Detail);
        var arena = checks.Single(c => c.Name == "Battle file");
        Assert.Equal(CheckLevel.Ok, arena.Level);
        Assert.Contains("Ranked Battle on Zigzag", arena.Detail);
    }

    [Fact]
    public void Game_running_elevated_is_explained()
    {
        var checks = GameDiagnostics.Run(FakeInstall(true, false), () => null, () => 1);
        Assert.Contains("administrator", checks.Single(c => c.Name == "Game running").Detail);
    }

    [Fact]
    public void Wrong_folder_is_a_problem()
    {
        var notGame = Path.Combine(Path.GetTempPath(), "foretop-tests", "not-a-game-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(notGame);
        var checks = GameDiagnostics.Run(notGame, () => null, () => 0);
        Assert.Equal(CheckLevel.Problem, checks.Single(c => c.Name == "Game folder").Level);
    }
}
