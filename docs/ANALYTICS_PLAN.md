# Plan: personal performance analytics and tier-filtered player statistics

Status: 2026-11-15. Section 4 (Live Battle tier filter, phases 6–7) is built (`Core/Stats/TierFilter.cs`,
Live Battle toolbar); the personal analytics (phases 2–5, 8) are not started. Based on Foretop 1.4.0 (`0bc405f`).

## 1. What exists today

| Area | Where | What it does now |
|---|---|---|
| Own battle results | `Core/Stats/BattleResultTracker.cs`, table `battle_results` | Reads the account's **mode totals** (Random / Ranked / Co-op) from `account/info` at battle start and again after the battle until the API has counted it; the difference = that battle's wins/losses/draws, damage, xp, frags, survived, planes. Shown on the Dashboard ("Your results"). 1 battle recorded so far. |
| Per-ship player stats | `PlayerStatsService`, `ships/stats` (cached as `ship_stats`) | Loaded for **every player in every live battle**: per ship `pvp` battles, wins, losses, draws, damage, xp, frags, survived, planes, max damage/frags. Used for PR and ship columns. |
| PR | `Core/Stats/PersonalRating.cs` | wows-numbers formula over per-ship totals with wows-numbers expected values (damage, frags, win rate per ship). Ships without expected values are left out of both sides. |
| Ship catalogue | `ShipCatalog` (API encyclopedia) + `ShipDataStore` (game files) | Tier, class, name for every current ship, test ships flagged. |
| Recorded battles | table `battles` (`BattleHistoryStore`) | Arena info + everyone's statistics at detection time (snapshot, never refreshed). |
| Analytics page | `StatisticsViewModel` / `StatisticsView` | Charts across recorded battles (types, maps, own ships, enemy classes, per day, team strength). |
| Charts | `App/Charts/Charts.cs` | Own BarChart and LineChart controls (no hover details yet). |
| Replays | `Core/Replays/*` | Full decoder for `.wowsreplay`; packet 0x22 (battle results) is not used yet. |

## 2. Data sources: what each can and cannot give

### A. Account mode totals (`account/info`), already used
Per battle: result (W/L/D), damage, xp, frags, survived, planes. Random, Ranked, Co-op only.
**Cannot**: spotting, potential damage, hits, time survived, team position. Only works when exactly the battles
between two readings are known (several battles counted at once become one combined row, as today).

**Found while researching:** for the recorded Jean Bart B battle the account-total difference gave 2,472 XP, but the
replay's own result data shows 1,498 in the places that look like base XP (the in-game missions counting "XP" also
show 1,498). The API's per-ship average for that ship is about 1,657, which fits ~1,500 per battle. So the current
"base XP" on the Dashboard may not be base XP. Phase 2 checks this against the per-ship difference and the
post-battle screen of a real battle, and relabels or fixes it.

### B. Per-ship totals of the ship played (`ships/stats`, own account), new
Same before/after method, but for the one ship played, with more fields (all `pvp.*` and `rank_solo.*`):
`damage_scouting` (spotting damage), `art_agro` + `torpedo_agro` (potential damage), `main_battery.hits/shots`,
`second_battery.hits/shots`, `torpedoes.hits/shots`, `ships_spotted`, `planes_killed`, `survived_battles`, `xp`, `frags`.
Gives per battle: spotting damage, potential damage, main battery and torpedo hits and accuracy.
**Cannot**: time survived, team position, credits.
Cost: one extra request at battle start and per poll for your own account only.

### C. The replay's battle-results packet (type 0x22), new, local
Written by the game into every replay (replays are already required). JSON with `privateDataList` (your
credits, XP, missions…), `commonList` (arena id, duration, map, mode) and `playersPublicInfo`: a **550-value record for
each of the 24 players**. Verified so far on your Tears of the Desert replay: position 0 = account id,
7 = ship id, 438 = damage (120,348, equals the API figure), 420/421 = killer's account and vehicle id, 15 = max HP;
428 looks like potential damage (1,157,550).
Could give per battle, for you **and everyone in the battle**: damage, kills, XP, spotting, potential damage, hits,
time survived, killer → team position by XP or damage, battle duration.
**Risk:** the record is positional and the game's scripts that name the positions are protected, so positions must
be identified from known values. They can move when the game updates. Plan: identify each needed position by
matching against source B over several battles, store the map per game build, check anchors (account id at 0,
ship id at 7, damage = API damage) on every replay, and treat the source as **unavailable for a build where the
anchors fail** until it is re-checked. Never guessed values.
Also allows **backfilling** past battles from replays still in the replays folder (only one there now).

### D. Locally recorded battles (`battles` table)
Map, mode, teams, everyone's stats at the time. Used to join battle info (map, type, ship, tier, class) to results.

### Not available anywhere
A complete battle history before Foretop started recording (the API has no per-battle history and its
`statsbydate` endpoint was removed). "Last 100 battles" will mean "last 100 battles Foretop recorded", and the UI says so.

## 3. Metrics: source and labelling

Every value carries its origin: **game** (replay results), **API** (difference of official totals), **calculated**
(Foretop arithmetic on those), or **unavailable** (shown as "—" with a reason, never 0).

| Metric | Single battle | Over a period |
|---|---|---|
| Result | API (A) or game (C) | Win rate = wins ÷ battles × 100 (calculated) |
| Damage, kills, survived, planes | API (A/B), game (C) | Averages = totals ÷ battles |
| Deaths | API: 1 − survived | Deaths per battle; K/D = kills ÷ deaths ("∞"/"—" when deaths = 0, labelled) |
| Base XP | game (C) / API (B, once verified) | Average |
| Spotting, potential damage | API (B), game (C) | Average |
| Main battery / torpedo accuracy | hits and shots from API (B) | Σhits ÷ Σshots (weighted, never an average of per-battle %) |
| Time survived | game (C) only | Average |
| Team position | game (C) only | Average position |
| PR | **calculated**: the wows-numbers formula applied to that battle's damage, kills and result against the ship's expected values, labelled "battle rating (calculated by Foretop)". Not an official number. | PR of the period = formula on the **summed** totals of the period's battles (same as wows-numbers does for a whole account), not an average of per-battle values |

## 4. Tier-filtered statistics in Live Battle (independent of A–C)

All inputs are already loaded: every player's per-ship `pvp` totals. A filter "Tier ≥ N" (or "Tier XI only") is a local
re-aggregation:

- battles, wins, losses, draws, damage, xp, frags, survived, planes = Σ over ships with tier ≥ N;
- win rate = Σwins ÷ Σbattles × 100; averages = Σtotal ÷ Σbattles; survival = Σsurvived ÷ Σbattles × 100;
  K/D = Σfrags ÷ (Σbattles − Σsurvived);
- PR = existing `PersonalRating.Compute` over the filtered ships (same formula, same expected values).
- Ships whose tier is unknown (removed ships missing from both catalogues) are left out of filtered values and
  counted in the tooltip ("3 ships of unknown tier not included").
- Unfiltered = current behaviour (account `pvp` totals), unchanged.
- The 7/28-day columns come from account snapshots, which have no per-ship split: under a filter they show
  "—" with "not available per tier" (could be added later by snapshotting per-ship totals).

UI: a "STATISTICS" toolbar above the team tables with **All tiers / V+ / VI+ / VII+ / VIII+ / IX+ / X+ / XI only**, a
view switch **Overall / Filtered / Both** (Both = two numbers per cell, e.g. `56.2 % · 52.8 %`), a "TIER VI+" badge
on the table headers while active, and the detail tooltip listing both blocks. The choice is remembered in settings.
No new API requests: switching is instant and does not touch detection.

## 5. Storage (SQLite, schema 5)

New table `personal_battles`, one row per own battle, written once and never recalculated from later API data:
`battle_key` (PK, joins `battles` and `battle_results`), `arena_id`, `started_at`, `ended_at`, `region`, `account_id`,
`map`, `battle_type`, `ship_id`, `ship_tier`, `ship_class` (tier/class stored, so later rebalances do not change
history), `result` (W/L/D), `damage`, `frags`, `survived`, `planes`, `base_xp`, `xp`, `spotting`, `potential`,
`mb_hits`, `mb_shots`, `torp_hits`, `torp_shots`, `life_time`, `team_position`, `rating` (calculated battle rating) and
the expected values used for it, plus a `source` mask per field group (api_account / api_ship / replay) and
`replay_file`. Nullable columns: null = unavailable. Indexes on `started_at`, `ship_id`, `ship_tier`.
Existing `battle_results` rows are migrated into it (fields they have only).

## 6. Analytics page layout

Analytics gets two tabs: **My performance** (new) and **Recorded battles** (today's page, unchanged).
My performance, top to bottom, panels collapsible (state remembered):

1. **Period bar**: Last battle · 5 · 10 · 20 · 50 · 100 · All · custom date range; plus filters (battle type, ship
   class, tier ≥) that apply to everything below. Shows "n battles recorded since …".
2. **Last battle**: date, map, type, ship (tier, class), result; damage, kills, deaths, XP, base XP, spotting, potential,
   planes, MB/torpedo hits and accuracy, survived + time survived, team position, battle rating (calculated). Each
   figure has its origin as a small tag (game / API / calculated).
3. **Summary cards** for the period: battles, W/L/D, win rate, kills, deaths, K/D, per-battle averages, PR (calculated),
   survival, spotting, potential, accuracies, time survived.
4. **Trends** (line charts with hover → battle details): win rate, damage, PR, kills/deaths/K/D, survival; switch
   **per battle / rolling (5 or 10) / cumulative average**; victory/defeat split and damage distribution as bar charts.
5. **Battle history table**: Date, Map, Ship, Tier, Class, Result, Damage, Kills, Deaths, Base XP, PR; sort, search,
   filter by ship / tier / type / result; CSV export; double-click opens the battle (existing battle view + the new
   result block, replay player if the replay exists).
6. **Compare periods**: two period pickers (presets: last 10 vs last 50, last 20 vs last 100, this month vs last
   month, Tier VI+ vs all) with the differences in win rate, PR, damage, kills, deaths, K/D, survival, green/red.
7. **Tier analysis**: I–V, VI, VII, VIII, IX, X, XI rows (battles, win rate, damage, base XP, kills, survival, PR), plus
   comparisons All vs VI+, VI+ vs VIII+, VIII+ vs X+. Two sources side by side: **your whole account** (from your
   per-ship API totals, all battles ever, same aggregation as section 4) and **recorded battles** (from the table).
8. **Ship performance**: per ship from recorded battles and from the API.

The page refreshes when the tracker reports a new result (existing `Changed` event). Aggregation runs off the UI
thread; at hundreds of rows it is instant, and it is cached per period.

## 7. Phases and checkpoints

| Phase | Work | Verifiable result |
|---|---|---|
| 1 | Research (done, this document) | – |
| 2 | Source B (per-ship before/after, extra fields), `personal_battles` table + migration, XP check | Next real battle: row with spotting/potential/hits matching the post-battle screen |
| 3 | Source C: replay results reader, position map for build 13533378 calibrated against B, anchors, backfill from replays | Same battle: replay values = API values; time survived and team position filled |
| 4 | Analytics "My performance": period bar, last battle, summary cards, history table + CSV, compare periods | Snapshots + unit tests of all aggregations (weighted, unavailable ≠ 0) |
| 5 | Charts with hover details and the three modes | Snapshots |
| 6 | Tier aggregation in Core (shared by Live Battle and Tier analysis) + unit tests on real cached ship stats | Tests compare filtered "All" with account totals |
| 7 | Live Battle toolbar, badge, Both view, tooltip, settings | Snapshot of a recorded battle with VI+ |
| 8 | Tier analysis + ship performance | Snapshots |
| 9 | Check against known numbers: your profile on wows-numbers / in game for filtered and overall values, a few real battles end to end | Written results in docs/TESTING.md |

Phases 6–7 do not depend on 2–5 and can come first if the Live Battle filter matters more.
Releases: one version per finished group (e.g. 1.5.0 = Live Battle tier filter, 1.6.0 = personal analytics).

## 8. Open points for the user

- Using replay result data (source C) means relying on calibrated positions that can break with a game update;
  Foretop would then fall back to API-only fields for that build. OK?
- Per-battle "PR" is not an official value; it would be shown as "battle rating (calculated by Foretop)".
- Old battles cannot be recovered beyond the replays still on disk.
