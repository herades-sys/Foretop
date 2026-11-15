# Changelog

## Unreleased

- README: screenshots of Live Battle, My performance, Replays and Ship Intelligence.
- Developer switch `--anonymize` (player names → "Player 1, 2…", clan tags hidden) for publishing screenshots.

## 1.6.0 — 2026-11-15

- **Analytics → My performance**: your own battles over any period (last battle, last 5 … 100, all, date range) with
  battle type, class and tier filters: last-battle report, period statistics, trend charts with hover details
  (per battle, rolling, overall average), battle history with search, filters, CSV export and click-to-open, period
  comparison, tier analysis (recorded battles and your whole account) and ship performance. Each value is tagged API,
  replay or calculated; unknown values show "—".
- New per-battle data: spotting damage, potential damage, main battery and torpedo hits/shots (your ship's totals
  before and after the battle), base XP, team position by damage, kills and time survived (from the replay, checked
  against the API), battle rating (PR formula on one battle, calculated). Stored with each result and not
  recalculated later.
- Dashboard: "base XP" was the XP the API counts, including Premium Account and bonuses; it is now labelled XP.
- Database: an error while opening is no longer taken as a damaged file straight away. Foretop retries, lets SQLite
  check the file, and only then sets it aside, moving the database and its log files together (a file in use stops
  the move). Before, a database could be set aside without its newest data.
- Line charts show the battle or value under the mouse.
## 1.5.0 — 2026-11-15

- **Live Battle: statistics filter by tier.** All tiers, Tier V+ … X+ or Tier XI only: battles, win rate, damage, XP
  and PR are recalculated from each player's per-ship statistics (weighted totals; PR over the same ships). Overall /
  Filtered / Both views, a badge on each team table, team averages and strength estimate follow; the player tooltip
  shows overall and filtered side by side with kills, survival and K/D. Remembered if you want. Recorded battles keep
  their filtered values. The old "Tier" filter for rows is now called "Ship tier".

## 1.4.0 — 2026-11-15

- **Updates from GitHub.** Foretop checks for a new release after start and once a day. An **Update** button in the
  top bar shows the release notes; **Install and restart** downloads the installer, verifies it against GitHub's
  SHA-256, installs it and starts the new version (the portable version opens the download page instead).
  **Skip this version**, **Later**, and in Settings → About: automatic check on/off and **Check now**.

## 1.3.0 — 2026-11-15

- **Sidebar can collapse** to the page icons only (Collapse at the bottom, or Ctrl+B); page names show as
  tooltips, and the choice is remembered.
- **Player Profiles: tier and class filter** above the ship list, like the ship carousel filters: pick any tiers
  (I–X, ★) and classes (DD, CA, BB, CV, SS); a line adds up battles, win rate and average damage of the ships
  shown.
- **Settings: font and text size.** Pick any font installed on Windows, and a text size from 90 to 150 % (the
  whole interface scales, so nothing gets crowded or cut off). Both apply on Save.
- **Licence:** Apache License 2.0 (LICENSE, NOTICE), with THIRD-PARTY-NOTICES.txt for the .NET runtime and packages;
  all three ship next to Foretop.exe.

## 1.2.1 — 2026-11-15

- **Replays: chat.** The battle chat (team and all) plays along with the replay: the latest lines at the bottom left
  of the map, as in the game (switch with **Chat**), and the full log up to the current moment beside the kill feed,
  with the sender in their team colour. Click a line to jump to it.

## 1.2.0 — 2026-11-15

- **Replays**: new page that lists the game's replays and plays one back on the minimap: ship tracks with heading,
  health and names, ships out of view at their last known position, sunk ships, capture points with owner, smoke,
  battle clock, team lists and kill feed; 1–40× speed. Decoded with the client's own entity definitions.

- **Test ships** (the game's "demo without stats" ships, 52 in the current build) show no damage and no statistics
  anywhere: only name, nation, tier and class are read from the game; their pages show a notice instead of
  characteristics, armour and equipment; they cannot be compared; per-ship player results for them are not shown and
  do not count towards PR; Dashboard results and Replays hide their damage and hit points.
- Ship Database: the list no longer comes up empty ("0 of 1,246") after the ship data is reloaded while the page is
  open (the nation filter lost its value).
- Armour viewer: thickness bands use five distinct hues (violet, teal, green, amber, red) instead of shades of
  amber, and plates within a band are a little darker or lighter by thickness, so neighbouring plates stand apart.
- Armour viewer: the hull can no longer be dragged out of its panel; it stays inside, or covers the panel when
  zoomed in.
- Settings: a saved application ID is no longer shown; the field appears again with "Change".

## 1.1.0 — 2026-11-15

Renamed from Broadside to Foretop (settings and data move over on first start) and
rebuilt on the Foretop design.

- New shell: sidebar with Dashboard, Live Battle, Player Search, Player Profiles,
  Ship Intelligence, Battle History, Analytics and Settings; back/forward, breadcrumb,
  Ctrl+K search for players and ships, API and region status.
- Ship Intelligence, read from the installed game client: Ship Database (1,246 ships,
  filters), ship profiles with shells and penetration by range, Armour Viewer (the
  hull's armour model in profile, plan, bow and rotatable 3D views, zone toggles,
  plate details), Ship Statistics (your results per ship), Ship Comparison (up to
  four ships) and Equipment & Upgrades. Read once per game build in the background.
- Your results on the Dashboard: victory or defeat, damage, base XP, frags and
  survival for each of your Random, Ranked and Co-op battles, with totals for this
  session and today (from your account totals before and after each battle).
- Map names from the game client ("Tears of the Desert" instead of "naval mission").
- Live Battle: long player and ship names end in "…" instead of being cut off; the
  "7 days" column only shows when someone has 7-day data.
- Settings: "Your account" for the account name.
- Themed scroll bars.

## 1.0.0 — 2026-11-14

First release.

- Live Battle: automatic detection from `replays\tempArenaInfo.json`, both teams with
  win rate, battles, damage, XP, Personal Rating, ship stats and recent activity;
  colour coding, sorting, class and tier filters, progressive loading.
- Team comparison with averages, composition and a strength estimate.
- Player profiles: overall, ranked and recent statistics, most played and best ships,
  class and tier breakdown, all ships, win-rate history, shared battles.
- Battle history (reopen with the statistics of the time, compare two) and a
  statistics page across recorded battles.
- Open any `.wowsreplay` to see its teams.
- SQLite cache with compression, 30-day pruning and offline fallback; priority request
  scheduling within Wargaming's rate limit.
- Settings: region, encrypted application ID, game folder, theme (System, Dark,
  Light), compact mode, start with Windows, cache times, detection check.
- Per-user MSI installer and portable zip; self-contained, no .NET install needed.

Known limitations: battle results are not available (not in the file the game
writes); recent statistics build up from Foretop's own snapshots because Wargaming
removed its recent-stats endpoint; no national flags (not provided by the API).
