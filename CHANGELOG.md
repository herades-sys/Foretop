# Changelog

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
