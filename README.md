# Broadside

A Windows battle monitor for World of Warships. When a battle loads, it shows both
teams with each player's statistics from the official Wargaming API.

It reads one file the game writes on its own (`replays\tempArenaInfo.json`). It
does not touch the game process, memory or network traffic. Design and research:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Requirements

- Windows 10/11, .NET 10 Desktop Runtime (the SDK to build)
- World of Warships with **replays enabled** (the default)
- A free **Wargaming application ID**: sign in at
  <https://developers.wargaming.net/applications/>, add an application of type
  *Mobile / Standalone*, paste the ID into Settings → Wargaming API.

## Build and run

```bash
dotnet build
```

```bash
dotnet run --project src/Broadside.App
```

```bash
dotnet test
```

Data lives in `%LOCALAPPDATA%\Broadside`: `broadside.db` (cache and battle
history), `settings.json`, `logs\`, `icons\` (ship icon cache) and `archive\`
(a damaged database is moved there, never deleted).

If the Wargaming API cannot be reached, Broadside shows the last cached statistics
and marks them "offline data". Only one copy runs at a time; starting it again brings
the open window to the front. Theme: Settings → Interface (System, Dark or Light).

## Using it

- **Live Battle** fills in automatically when a battle loads. Rows fill in as
  data arrives: win rate and battles first, then PR and ship stats, then recent
  activity.
- **Open replay…** shows the teams of any `.wowsreplay` or `tempArenaInfo.json`,
  handy when the game is not running.
- Hover a row for the full breakdown, with the source of each number. Double-click
  opens the player's profile in Broadside; right-click for the official profile
  or wows-numbers.
- **Comparison** (switch above the tables) puts the two teams side by side: averages,
  composition and the strength estimate.
- **Player Profiles**: search any player. Overall and recent stats, most played and
  best ships, battles by class and tier, all ships, win-rate history (from
  Broadside's own snapshots) and the recorded battles you shared.
- **Battle History**: every detected battle and opened replay, with team averages at
  the time. Double-click to see the full rosters again; select two to compare.
- **Statistics**: across recorded battles: your ships, maps, battle types, enemy
  classes, team strength per battle and the players you meet most often. Battle
  results are not in the file the game writes, so there are no win/loss figures.

### Colours

| Metric | Red | Yellow | Green | Purple |
|---|---|---|---|---|
| Win rate | < 47 % | 47–53 % | 53–60 % | ≥ 60 % |
| Personal Rating | < 1100 | 1100–1550 | 1550–2100 | ≥ 2100 |
| Damage (vs. expected for the ships played) | < 85 % | 85–115 % | 115–150 % | ≥ 150 % |

### Where the numbers come from

- **Official**: battles, wins, damage, XP, frags, survival, planes, per-ship stats.
- **Derived**: every average and rate (arithmetic on official values).
- **Third-party**: Personal Rating and the damage ratio, using the
  [wows-numbers](https://wows-numbers.com) expected values.
- **Recent (7 / 28 days)**: Broadside's own snapshots of players it has seen
  before. Wargaming removed its recent-stats endpoint, so a player's recent
  stats appear once Broadside saw them at least 7 days earlier.

## Developer switches

```bash
Broadside.App.exe --open path\to\file.wowsreplay --page Live --snapshot out.png --wait 3
```

`--snapshot` renders the window to a PNG and exits (used for UI checks without the game).
Also: `--profile <name>`, `--compare`, `--open-latest-history`, `--scroll <px>`, `--light`,
`--switch-theme <Dark|Light>`, `--diag` (logs memory every 30 s), `--software` (software rendering).
