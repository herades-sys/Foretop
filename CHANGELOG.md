# Changelog

## 1.8.1 — 2026-11-18

- **Live Battle: ship battles and ship PR in their own columns.** SHIP BTL (battles in the ship of this battle) and
  SHIP PR (Personal Rating in that ship) sit next to SHIP WR; before, ship battles were a small grey number after the
  ship win rate and ship PR was only in the tooltip. Ship PR is coloured from 10 battles in the ship. In a narrow
  window they step aside after 7 days, XP and ship win rate, before PR and damage.

## 1.8.0 — 2026-11-17

- **Startup screen.** While Foretop opens it checks, and shows: its data, the game installation, the Wargaming API
  (one light request with your application ID: connected, offline, ID missing or rejected) and GitHub for a newer
  Foretop. It hands over to the main window by itself (a few seconds longer when something needs attention; a click
  or Enter opens Foretop at once). Each check gives up after 6 s and carries on in the background. Not shown when
  Foretop starts minimised; Settings → Interface can turn it off.
- The theme generator is now in the repository (`tools/themes/make_theme_xaml.py`).

## 1.7.1 — 2026-11-16

- **Live Battle tables: full player and ship names.** Columns are sized to what they show (measured, not fixed
  widths), with the same widths in both team tables so they line up; spare width goes to the player and ship names.
  Ship names like Rhode Island, Petropavlovsk, Ignis Purgatio, Charles Martel and Oktyabrskaya Revolutsiya are no
  longer cut. When the window is too narrow, less important columns step aside first (7 days, XP, ship win rate, PR,
  damage; the team header says which) and come back when there is room; battles and win rate always stay. Only in a
  very narrow window are names shortened, splitting the space so the fewest are cut; hovering a row shows the player
  and ship in full. Number headers are right-aligned over their values.
- Developer switch `--window` can now be larger than the monitor (tests of 2560×1440 and 3440×1440 on a smaller screen).

## 1.7.0 — 2026-11-16

- **Replays from an older game version** are read with that version's definitions when the game still has its files
  (it keeps the previous version after an update): player names, teams and events are no longer lost. Otherwise the
  replay opens with a note that some details may be missing. Repeated replay warnings are shown once.
- Test ships in Live Battle: account values only, a TEST tag before the ship name, and "—" instead of 0 battles.
- **Three new themes** in Settings → Theme, besides Dark and Light: **Steel** (today's dark look with higher contrast and
  team colours that differ in lightness), **Chart Room** (light, paper and navy ink with brass, serif headings) and
  **Combat Information Center** (near-black, sharp corners, Bahnschrift, blue vs orange teams that colour-blind players
  tell apart). Themes now set corner radii, interface and heading fonts and table row height, not only colours; the
  font chosen in Settings still overrides the theme's. The replay map keeps readable team colours on light themes.
- Filter chips keep their height in taller rows, show the checked state clearly and react to the mouse.
- Player Search finds a player by account id (digits, as in profile links).
- Profiles opened from a battle use that battle's server.
- A game update while Foretop runs: the previous complete game version is used until the new one is fully written, and
  Ship Intelligence reads the new version when shown.
- Battle results of one check are stored together (all or nothing), and the API calls no longer hold back the start of
  the next battle.
- Logs older than 30 days are packed into one zip per month under the data folder's archive (nothing is lost).
- Ship profile: other torpedo modules of the ship are named next to the torpedoes.
- Replays keep the images of at most six maps in memory.
- **Analytics → My performance: filter toolbar and tier analysis.** Source (whole account or recorded battles), tiers
  (several, Shift+click ranges, VI+ / VIII+ / X+ shortcuts), minimum battles per ship, period, class, nation, ships
  and battle type, shown as removable labels; every section follows. New: best and worst performing ships (PR, win
  rate, damage, battles), average damage and win rate per ship / tier / class, ship comparison with totals, tier
  performance table (click a tier to filter), tier comparison (two or three groups, absolute and relative
  differences), PR over time for several tier lines with the account PR as reference. Each chart can add its own class
  filter, which only narrows. All values are totals ÷ battles; missing values stay "—".
- Replays: damaged, cut-off or unusual replay files show a message instead of the error dialog; one unreadable replay
  no longer stops reading the others for battle details; very large unpacked sizes are refused. Playback pauses when
  you leave the page.
- API requests: the request queue can no longer stop (which would have left every lookup waiting).
- Updates are installed only with GitHub's checksum and only from GitHub over https.
- Refresh on a recorded battle never saves, also when pressed twice, and uses the battle's server.
- Ship Database: most premium ships (game group "special") are now "Premium", not "Special"; ship data is read again
  once for this.
- Live Battle: "7 days" / "28 days" show "—" instead of months of activity when the last snapshot is old; class and tier
  filters also apply to ships the API list omits. Profiles opened from a battle use the real name with --anonymize.
- Player Search checks the name format before asking the API; offline players without a cache say "unavailable"
  instead of "no data"; Ship Statistics no longer keeps the previous account's figures; a battle whose end was missed
  no longer holds back later results; a damaged game index during a game update is a readable error.
- Database: indexes for the history list and the result checks (schema 6).
- **Replays: positions were off on most maps.** The map size was read from the game's map settings in only one of
  the two forms the game uses; 53 of 58 maps fell back to one fixed size, so ships, capture points and smoke were
  drawn up to 14 % too far out (maps of 32 km) or pulled towards the centre (smaller maps). Fixed; every element now
  goes through one shared transformation.
- Replays: zoom at the pointer (wheel, + / −, up to 8×), drag to move, double-click for the whole map, overview
  while zoomed; **Full view** (F); **Debug** overlay (bounds, origin, coordinates, scale, zoom); the timeline gets its
  own full-width row; side panels slimmer on small windows; trails meet their ship.
- **Refresh** on a recorded battle no longer replaces the statistics recorded at the time, and opening the replay of
  a battle recorded live keeps the live statistics.
- Saving Settings no longer clears the account name learned from a battle.
- Battles are recorded in History also without an application ID, and when their statistics load is interrupted.
- Cancelled API requests (an abandoned battle, an old search) are dropped at once instead of using request slots.
- Faster start (ship data read in the background) and no UI pauses for a profile's recorded battles; no reloads every
  2 minutes after a battle unless a result changed; missing ship icons are not re-requested on every update.
- The update notice comes back after a restart. Map names on Dashboard and History use the game's names. Fixes for
  the 7-day column under a tier filter, stale-cache dates, date sorting on the Dashboard, CSV export to an open file,
  chart hover after a data change, chart fonts, "% WR" without data, plural texts.
- Best performing ships also ranks ships the API's ship list omits (tier from the game files).
- Audit report: docs/AUDIT_2026-11-16.md.
- README: screenshots of Live Battle, My performance, Replays and Ship Intelligence.
- Player Profiles → **Best performing ships**: filter by tier (All tiers, or any of I–XI together) and minimum
  battles (1+, 5+, 10+, 20+, 50+, 100+ or custom; 20+ by default). The ranking, title and scale follow at once; the
  line under the filters shows the active filters, how many ships match and which ships are not ranked (no PR,
  unknown tier). Kept for the session. PR per ship is unchanged.
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
