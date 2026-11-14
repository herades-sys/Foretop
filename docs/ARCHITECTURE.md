# Broadside — technical architecture

Broadside is a standalone Windows app that shows statistics for every player in
the user's current World of Warships battle. It reads one file the game writes
on its own, calls the official Wargaming API, and never touches the game
process.

Status (2026-11-14): Phases 1–5 done (Phase 3's national flags excluded:
the API has no flag images). Tested against the live EU API with a real
application ID and a roster of real players; values match the raw API. Not yet
tested against a running game — see "Open items" at the end.

---

## 1. How the battle roster is obtained (the key feasibility question)

**The Wargaming API has no "current battle" endpoint.** Every existing monitor
(WoWS Monitor, Potato Alert, Matchmaking Monitor, quartersbrief, WoWSP) uses the
same source instead:

> When a battle loads, the game client writes `tempArenaInfo.json` into its
> `replays` folder. It is deleted (or left stale) when the battle ends.

This is a plain file the client writes for its own replay system. Reading it
needs no injection, no memory access and no network sniffing. It only exists
when **replays are enabled** in the game settings (the current default).

### File location

| Client | Path |
|---|---|
| WG launcher / Steam (EU, NA, Asia) | `<game root>\replays\tempArenaInfo.json` |
| "Save replays per version" setting | `<game root>\replays\<version>\tempArenaInfo.json` |
| Some CN builds | `<game root>\tempArenaInfo.json` |

The game root is found, in order, from:
1. the user's manual setting;
2. the running `WorldOfWarships64.exe` (`<root>\bin\<build>\bin64\…`, so three
   folders up);
3. common install folders (`C:\Games\World_of_Warships`,
   `<Steam library>\steamapps\common\World of Warships`, every Steam library
   listed in `libraryfolders.vdf`).

### File format (verified against a real file)

```json
{
  "matchGroup": "pvp", "gameMode": 7, "gameLogic": "Domination",
  "mapDisplayName": "42_Neighbors", "mapId": 26, "duration": 1200,
  "name": "12x12", "scenario": "Domination", "playersPerTeam": 12,
  "teamsCount": 2, "dateTime": "19.12.2016 17:47:18",
  "playerName": "SomeCaptain", "playerVehicle": "PRSC107-Schors",
  "clientVersionFromXml": "0, 5, 15, 216844",
  "vehicles": [
    { "shipId": 4288559088, "relation": 1, "id": 537655300, "name": "Nimitz_best" }
  ]
}
```

| Field | Meaning |
|---|---|
| `vehicles[].name` | Player nickname. **The only reliable player key.** |
| `vehicles[].relation` | `0` = you, `1` = ally, `2` = enemy. In operations, `≤ 1` is the friendly side. |
| `vehicles[].shipId` | Ship ID, the same ID the Wargaming encyclopedia uses. |
| `vehicles[].id` | Battle-local entity ID in current clients. **Not** an account ID. Negative for bots. |
| `matchGroup` | Battle type: `pvp` (Random), `ranked`, `clan`, `pve` (Co-op), `event`, `brawl`, … |

What the file does **not** contain: battle ID, account IDs, region, divisions,
clan tags. Consequences:

- **Account IDs** are resolved by nickname with `account/list?type=exact`
  (up to 100 names in one call) on the configured region.
- **Battle ID** is not available. Broadside uses a synthetic key: hash of
  `dateTime` + sorted roster. It is labelled as such.
- **Battle status** (alive/sunk) is not in the file and not exposed anywhere
  legitimately readable while the battle runs. Not implemented.
- **Bots** are recognised by `:Name:` style nicknames or negative `id`, and are
  never looked up.

### Pitfalls the watcher must handle

- **Stale files.** A leftover `tempArenaInfo.json` from an old battle must not
  be shown as live. Only files written after the game process started (and
  while it runs) count as a live battle.
- **Partial writes.** The file is read with retries until it parses.
- **Two homes.** Watch `replays\` recursively plus the game root.
- **FileSystemWatcher misses** events on some setups, so a 2-second mtime poll
  runs as a backup.
- Some clients have been reported not to write the file at all. Then
  detection is impossible without the replay file, and the app says so.

## 2. Wargaming API (verified)

Hosts: `https://api.worldofwarships.eu`, `.com` (NA), `.asia`. All endpoints
need an `application_id`, which each user registers for free at
<https://developers.wargaming.net/> (choose type "Standalone"). The shared
`demo` ID is blocked (verified: `407 DEMO_APPLICATION_IS_BLOCKED`), so the app
cannot ship a working default ID and must not ship a hardcoded one.

| Endpoint | Batch limit | Used for |
|---|---|---|
| `wows/account/list/` (`type=exact`) | 100 names | nickname → account ID |
| `wows/account/info/` | 100 account IDs | totals, `hidden_profile`, `created_at`, `last_battle_time`; `extra=statistics.rank_solo,…` for other modes |
| `wows/ships/stats/` | **1 account** (up to 100 ship IDs) | per-ship stats; needed for PR and "most played" |
| ~~`wows/account/statsbydate/`~~ | — | **Removed by Wargaming**: returns `404 METHOD_NOT_FOUND` (verified 2026-11-14) |
| `wows/clans/accountinfo/` (`extra=clan`) | 100 account IDs | clan tag |
| `wows/encyclopedia/ships/` | 100 per page, paged | ship name, tier, class, nation, premium, icons |
| `wows/encyclopedia/info/` | — | game version, to know when ship data changes |

**Rate limit:** about 10 requests/second per IP for standalone applications,
20/s for server applications. Exceeding it returns error `407
REQUEST_LIMIT_EXCEEDED`. Broadside defaults to 8/s and backs off on 407.

**Hidden profiles:** `account/info` returns `hidden_profile: true` and no
statistics; `ships/stats` omits them and lists them in `meta.hidden`.

**Request budget for one 12v12 battle (cold cache):** 1 `account/list` + 1
`account/info` + 1 `clans/accountinfo` + one `ships/stats` per public player.
Measured 2026-11-14 on EU with 22 real players: 23 requests, 3.2 s; the same
battle again from cache: 0 requests, 0.1 s.

Verified live: `account/list?type=exact` accepts at least 60 comma-separated
names in one call (the 24-character limit only applies to `startswith`); the same
application ID works on EU, NA and Asia; hidden profiles come back with `null` in
numeric fields such as `leveling_tier`.

## 3. Statistics: what is official, what is derived, what is third-party

Every number in the UI carries one of three origins, shown in its tooltip and
in the profile view:

| Origin | Statistics |
|---|---|
| **Official** (raw API values) | battles, wins, losses, draws, damage dealt, XP, frags, survived battles, planes destroyed, account created, last battle, per-ship battles/wins/damage/XP/frags |
| **Derived** (simple arithmetic on official values) | win rate, average damage, average XP, average frags, survival rate, average planes, per-ship averages, team averages |
| **Third-party** | **Personal Rating (PR)**, using the expected values published by wows-numbers at `https://api.wows-numbers.com/personal/rating/expected/json/` (verified reachable, updated daily) |

### Personal Rating

Per player, summed over every ship with expected values:

```
expDmg   = Σ battles_i · expected_damage_i     (same for frags and wins)
rDmg     = actualDmg / expDmg
nDmg     = max(0, (rDmg   - 0.4) / 0.6)
nFrags   = max(0, (rFrags - 0.1) / 0.9)
nWins    = max(0, (rWins  - 0.7) / 0.3)
PR       = 700·nDmg + 300·nFrags + 150·nWins
```

Ship-level PR uses the same formula for one ship. Colour bands: < 750 bad,
750 below average, 1100 average, 1350 good, 1550 very good, 1750 great,
2100 unicum, 2450 super unicum.

### Recent performance

The API has **no recent-stats endpoint**: `account/statsbydate` was removed
(`404 METHOD_NOT_FOUND`, verified 2026-11-14). Broadside therefore stores a
snapshot of each player's totals whenever they change and computes "last 7 days"
and "last 28 days" from the difference. A player's recent stats appear only once
Broadside has seen them at least 7 days earlier, and the label shows the real
span covered.

### Not supported

- **Battle results** (win/loss) are not in `tempArenaInfo.json`. They exist only
  inside the replay's packet stream, which Broadside does not decode, so the
  history and statistics pages cover who you met, not whether you won.

- **WTR** (warships.today): the formula and its expected values are not public
  and the site is gone. Not implemented; PR is the community standard.
- **Per-battle history of other players**: the API exposes totals only.
- **Win-rate progression** before Broadside started watching a player: only
  available from Broadside's own snapshots, so it grows over time.

## 4. Technology

| Choice | Why |
|---|---|
| C# / .NET 10 (LTS) | Runtime already on the PC; fast startup; strong async |
| **WPF** (not WinUI 3) | Mature, no MSIX packaging, lower memory, mature DataGrid and virtualisation, simple single-file publish |
| CommunityToolkit.Mvvm | Source-generated MVVM, no reflection cost |
| Microsoft.Data.Sqlite | Small, no ORM; a handful of tables |
| System.Text.Json | Built in, source-generated contexts |
| DPAPI (`ProtectedData`, current user) | Stores the application ID encrypted at rest |
| Microsoft.Extensions.Logging + small JSON-lines file sink | Structured logs, no heavy logging framework |
| xUnit | Tests for parser, PR, rate limiter, cache |

## 5. Solution layout

```
broadside/
  docs/ARCHITECTURE.md
  src/Broadside.Core/          net10.0, no UI
    Game/      GameLocator, ArenaInfo(+Parser), BattleWatcher, BattleTypes
    Api/       WgApiClient, RequestScheduler (priority + rate limit + retry), DTOs, Region
    Stats/     PersonalRating, ExpectedValuesProvider, ShipCatalog, PlayerStatsService
    Data/      Database (schema/migrations), ApiCache, SnapshotStore, BattleHistoryStore
    Settings/  AppSettings, SettingsStore, ISecretProtector
  src/Broadside.App/           net10.0-windows, WPF
    ViewModels/  Shell, LiveBattle, PlayerRow, Settings, …
    Views/       XAML per screen; Themes/Dark.xaml
    Services/    DpapiSecretProtector, file logger
  tests/Broadside.Tests/
```

### Data flow for one battle

```
tempArenaInfo.json ──► BattleWatcher ──► ArenaInfo (roster, ship IDs, type)
                                              │
                         LiveBattleViewModel ◄┘  (switches to Live Battle tab)
                                              │
                    PlayerStatsService.LoadAsync(roster)
                     1. names → IDs     (cache, then account/list exact)   HIGH
                     2. account/info    (cache, then 1 batched call)       HIGH
                        + recent stats from local snapshots
                     3. clans           (1 batched call)                   HIGH
                     4. ships/stats     (per player; PR + current ship)    HIGH
                              │ each step raises PlayerUpdated
                              ▼
                    rows update in place; status per row
                              │
                    BattleHistoryStore.Save(snapshot of roster + stats)
```

### Request scheduler

- One dispatcher loop; work items in a priority queue (High = current battle,
  Normal = profile views, Low = recent stats and background refresh).
- Token-bucket rate limiter (default 8/s, configurable).
- Retries with exponential backoff and jitter on network errors, HTTP 5xx,
  `407 REQUEST_LIMIT_EXCEEDED` and `504 SOURCE_NOT_AVAILABLE`; no retry on
  invalid-application-ID or other client errors.
- A new battle cancels Low-priority work left over from the previous one.

### SQLite (`%LOCALAPPDATA%\Broadside\broadside.db`)

| Table | Content | Default expiry |
|---|---|---|
| `api_cache` | raw JSON per (region, endpoint, key) + fetched time | account/info, ships/stats: 1 h; clans: 24 h |
| `accounts` | region, nickname (case-folded) → account ID | 30 days |
| `ships` | encyclopedia rows | refreshed when game version changes or after 7 days |
| `expected_values` | PR expected values | 24 h |
| `player_snapshots` | totals per player per fetch | kept; basis for trends |
| `battles` | history: time, type, map, source (live / replay), arena JSON, every player's stats at the time (`BattleSummaryCodec`) | kept |

All expiries are user-configurable.

## 6. Security and policy

- No game files are written; the game process is only queried for its path.
- No memory access, injection, packet capture or automation.
- No credentials in the source or the repository. The application ID is
  entered by the user and stored with DPAPI.
- Network traffic: only the Wargaming API and the wows-numbers expected-values
  file, both cached.

## 7. Phase plan

| Phase | Content | State |
|---|---|---|
| 1 | Research, this document | done |
| 2 | Solution, game locator, battle watcher, parser, API client + scheduler, cache, stats service | done (47 unit tests) |
| 3 | Live Battle screen: two teams, sorting, filtering, loading states, colours | done; national flags left out (the API has no flag images) |
| 4 | Team comparison + charts, player profile, recent stats, battle history | done 2026-11-14: comparison view, profile page, history (revisit + compare two), statistics page; 50 unit tests |
| 5 | Performance pass, error handling polish, logging review | done 2026-11-14 (see "Phase 5 results"); 53 unit tests |
| 6 | Tests against real battles, verification vs. official API, installer, user docs | |

### Phase 5 results (Release build, this PC, 2026-11-14)

| | Before | After |
|---|---|---|
| Start to first render | 1.0–1.3 s | 0.76–0.95 s |
| Private memory, battle shown | 103–107 MB | 103–105 MB (managed heap ≈ 20 MB; the rest is WPF/.NET) |
| Idle CPU, settled | 0.26–1.09 % of one core | 0.31 % of one core |
| History, 1000 battles | 24 MB, full re-parse on each visit (≈ 0.6 s) | 3.4 MB, only new or changed battles re-parsed |

Changes:
- **Watcher:** the uninstall-registry scan runs once per session, the folder scan at
  most every 5 minutes, and a running game is tracked through its process handle.
- **Ship icons:** downloaded once into `icons\`, decoded at 72 px wide.
- **Cache:** API responses and saved battle stats are Brotli-compressed (schema v3);
  responses unused for 30 days are pruned at startup (regenerable cache only).
- **Offline:** when the API stays unreachable after retries, the last cached
  statistics are shown and marked "offline data"; names never seen before show an
  explicit error instead of "not found".
- **Robustness:** an unreadable database is moved to `archive\<time>\` and a new
  one created; one instance per Windows session (a second launch brings the first
  to the front); unobserved task and unhandled exceptions are logged.
- **Theme:** System / Dark / Light, switchable while running; the title bar
  follows the theme.
- Software rendering (`--software`) saves about 20 MB but costs smoothness, so it
  stays a diagnostic switch rather than the default.

### Open items needing the user

- **A PC with the game installed** to test against real battles. The game is
  not installed on the development PC.
