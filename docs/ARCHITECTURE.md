# Foretop — technical architecture

Foretop is a standalone Windows app that shows statistics for every player in
the user's current World of Warships battle. It reads one file the game writes
on its own, calls the official Wargaming API, and never touches the game
process.

Status (2026-11-14): Phases 1–6 done, apart from a test on a real game client (Phase 3's national flags excluded:
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
- **Battle ID** is not available. Foretop uses a synthetic key: hash of
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
REQUEST_LIMIT_EXCEEDED`. Foretop defaults to 8/s and backs off on 407.

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
(`404 METHOD_NOT_FOUND`, verified 2026-11-14). Foretop therefore stores a
snapshot of each player's totals whenever they change and computes "last 7 days"
and "last 28 days" from the difference. A player's recent stats appear only once
Foretop has seen them at least 7 days earlier, and the label shows the real
span covered.

### Not supported

- **Other players' battle results** are not available: `tempArenaInfo.json` has none,
  and the replay's results block (block 1) was empty in a real Random battle on
  2026-11-15 (block 2 only holds the recording player's account ID). Your **own**
  results come from the API instead (below). The replay's packet stream does end
  with a battle-results packet (section 9), which could later fill in results for
  battles Foretop did not watch.

### Own battle results (`BattleResultTracker`, Dashboard)

At battle start Foretop fetches the account's totals for the battle's mode
(`account/info`, `statistics.pvp` / `rank_solo` / `pve`, never from the cache) and
stores them as the baseline in `battle_results`. After the battle ends it polls
(20 s, 40 s, 60 s, 90 s, then every 2 min for 30 min) until the totals grow; the
difference is the result. `ResultMath.Apply` handles the edge cases: a battle not
yet ended is never resolved; later open battles take the new totals as their
baseline; several battles counted at once become one combined row plus merged rows,
so session totals stay exact. Verified 2026-11-15: the API counted a Random battle
30 s after it ended (one battle, one win, 120,348 damage).

- **WTR** (warships.today): the formula and its expected values are not public
  and the site is gone. Not implemented; PR is the community standard.
- **Per-battle history of other players**: the API exposes totals only.
- **Win-rate progression** before Foretop started watching a player: only
  available from Foretop's own snapshots, so it grows over time.

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
foretop/
  docs/ARCHITECTURE.md
  src/Foretop.Core/          net10.0, no UI
    Game/      GameLocator, ArenaInfo(+Parser), BattleWatcher, BattleTypes
    Api/       WgApiClient, RequestScheduler (priority + rate limit + retry), DTOs, Region
    Stats/     PersonalRating, ExpectedValuesProvider, ShipCatalog, PlayerStatsService
    Data/      Database (schema/migrations), ApiCache, SnapshotStore, BattleHistoryStore
    Settings/  AppSettings, SettingsStore, ISecretProtector
    GameData/  GameFileSystem (idx/pkg), Unpickler, GameParamsReader, GameTexts, ShipExtractor,
               ShipDataStore (per-build cache, helper process), ArmourGeometry, Ballistics, ArmourMaterials
    Replays/   ReplayFile (decrypt), EntityDefs + BigWorldReader (entity definitions), ReplayParser, ReplayLibrary
  src/Foretop.App/           net10.0-windows, WPF
    ViewModels/  Shell, LiveBattle, PlayerRow, Settings, …
    Views/       XAML per screen (Ship Intelligence: ShipIntelligenceView hosts ShipDatabase/Profile,
                 ShipArmour (+ ArmourCanvas), ShipStatistics, ShipComparison, ShipEquipment); Themes/
    Services/    DpapiSecretProtector, file logger
  tests/Foretop.Tests/
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

### SQLite (`%LOCALAPPDATA%\Foretop\foretop.db`)

| Table | Content | Default expiry |
|---|---|---|
| `api_cache` | raw JSON per (region, endpoint, key) + fetched time | account/info, ships/stats: 1 h; clans: 24 h |
| `accounts` | region, nickname (case-folded) → account ID | 30 days |
| `ships` | encyclopedia rows | refreshed when game version changes or after 7 days |
| `expected_values` | PR expected values | 24 h |
| `player_snapshots` | totals per player per fetch | kept; basis for trends |
| `battles` | history: time, type, map, source (live / replay), arena JSON, every player's stats at the time (`BattleSummaryCodec`) | kept |
| `battle_results` | your own result per battle: mode, ship, baseline totals, difference, status (Pending / Ok / Combined / Merged / Unavailable / Expired) | kept |

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
| 6 | Tests against real battles, verification vs. official API, installer, user docs | done 2026-11-14 except the real-client test (no game on the dev PC; checklist in docs/TESTING.md); 58 unit tests |

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

### Phase 6 results

- **Release:** self-contained single-file exe (win-x64, ReadyToRun, uncompressed:
  starts in about 0.57 s vs 0.82 s compressed); per-user MSI (WiX 5, 49 MB) and
  portable zip (61 MB) from `builduild.ps1`.
- **Installer test (Windows 11):** install, launch, uninstall (user data kept),
  reinstall. One uninstall right after running the exe left the file behind (sharing
  violation, most likely a virus scan); a repeat was clean. Documented in the user
  guide.
- **Detection without the game:** a stand-in process named `WorldOfWarships64.exe` in a
  fake install folder plus a written arena file: battle detected about 1 s after the
  write, automatic switch to Live Battle, battle end detected when the process exited.
  This found and fixed a sorting bug (the DataGrid cleared the view's sort when the
  Live page was created after a battle had loaded).
- **Detection check** in Settings (`GameDiagnostics`): game process, folder, replays,
  arena file, API, watcher.

### Open items needing the user

- **A PC with the game installed** to test against real battles. The game is
  not installed on the development PC.

## 8. Ship Intelligence (game-client data)

Read-only, from the installed client; details and verified values in [GAME_DATA.md](GAME_DATA.md).

1. **Extraction, once per game build.** `ShipDataStore.EnsureAsync` finds the newest build. When no cache of that build
   and format exists, it starts `Foretop.exe --extract-ship-data <game> <out>` as a child process at below-normal
   priority (GameParams needs about 950 MB for a few seconds; a separate process gives that memory back). The helper
   reports `progress d t` lines; the UI shows the design's "Reading ship data" state. The result is
   `%LOCALAPPDATA%\Foretop\shipdata\<build>.json.br` plus `<build>-images\preview|icon\<index>.png`. During an update
   the previous build's data stays in use with a banner.
2. **Ship records** (`ShipExtractor`): stock and fully upgraded configuration from the module tree, shells, AA,
   torpedoes, consumables, applicable upgrades, armour plates and the hull model path.
3. **Armour model on demand** (`ShipDataStore.LoadArmourAsync` → `ArmourGeometry.LoadHull`): opens the file index once
   (0.5 s), then reads the hull's main `.geometry` (Yamato 6,978 triangles in about 80 ms); the last 24 hulls stay in
   memory. `ArmourCanvas` draws them with an orthographic projection and the painter's algorithm, one colour per
   thickness class with simple shading; click selection is a point-in-triangle test from the nearest triangle back.
4. **Own results** (Ship Statistics): `PlayerStatsService` for the account in Settings, joined to the game records by
   `ship_id` (the GameParams `id`).

## 9. Replays (minimap player)

The Replays page lists `<game>\replays\**\*.wowsreplay` (header block 0 only, so the list is instant) and plays one
back on the map. Read-only; format details in [GAME_DATA.md](GAME_DATA.md#replays-wowsreplay).

1. **File** (`ReplayFile`): JSON header blocks, then the packet stream: Blowfish-ECB with each 8-byte block XOR-ed with
   the previous plain block, then zlib (BouncyCastle for Blowfish, .NET for zlib). A 13-minute Random battle: 2.0 MB
   file → 6.6 MB of packets, decoded in about 0.3 s.
2. **Entity definitions** (`EntityDefs`): `scripts/entities.xml`, `entity_defs/*.def`, `interfaces/*.def` and
   `alias.xml` from the client. Method and property numbers are computed from them (stable sort by encoded size,
   inherited definitions first), so a patch that adds a method does not break decoding. `BigWorldReader` decodes
   values of any defined type.
3. **Parser** (`ReplayParser`): positions (`Position` for other ships, `PlayerPosition` for the controlled one),
   visibility spans (entity create … leave), `Vehicle.health`/`maxHealth`/`teamId`, `Avatar.receiveVehicleDeath`,
   capture points (`InteractiveZone` with control-point index and owner team), smoke (`SmokeScreen.points`), the battle
   clock (`BattleLogic.timeLeft`). Players come from the arena JSON and are linked to their vehicle entity through the
   pickled player list of `Avatar.onArenaStateReceived`, whose keys are found by value (name, vehicle id, account id)
   because their numbering changes between versions.
4. **Map** (`ReplayLibrary`): `spaces/<map>/minimap_water.png` + `minimap.png` (760 px) and the playable square from
   `space.settings` (chunk bounds minus two chunks per side; ±700 on Tears of the Desert). Checked by sampling 8,345
   track points: 0.26 % fall on land at this scale, 2–15 % at 0.8–1.2× or with the axis flipped.
5. **Player** (`ReplaysViewModel`, `ReplayScene`, `ReplayCanvas`): interpolates positions between packets (not across
   gaps over 6 s), draws ships the client could not see hollow at their last known position and sunk ships as a
   cross; 1–40× speed, team lists with health, kill feed by battle clock.

Not decoded yet: shells, torpedoes, planes, capture progress, ribbons and chat. The large packet at the end of the
stream (type 0x22) is the battle-results JSON with the recording player's own credits and XP; it is not used yet.
