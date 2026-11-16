# Foretop user guide

Foretop shows statistics for every player in your World of Warships battle, the
moment the battle loads. It runs beside the game and changes nothing in it.

## 1. Install

Pick one:

- **Installer** — run `Foretop-<version>-x64.msi`. It installs for your Windows
  account only (no administrator prompt) into `%LOCALAPPDATA%\Programs\Foretop` and
  adds Foretop to the Start menu. Uninstall from *Settings → Apps*.
- **Portable** — unzip `Foretop-<version>-portable-x64.zip` anywhere and run
  `Foretop.exe`.

Nothing else is needed; .NET is built in. Windows 10 or 11, 64-bit.

Your settings, cache, battle history and logs live in `%LOCALAPPDATA%\Foretop` and
stay there when you uninstall or update.

## 2. First start

1. **Get a Wargaming application ID** (free, two minutes):
   sign in at <https://developers.wargaming.net/applications/> with your game
   account, click *Add application*, choose type **Mobile / Standalone**, save, and
   copy the 32-character ID.
2. In Foretop open **Settings**, pick your **server region**, paste the ID, click
   **Test** (it should say "✓ OK — game version …"), then **Save**.
3. **Replays must be enabled in the game** (it is the default). The game only
   writes the battle roster while replays are on.
4. Click **Run check** at the bottom of Settings. Every line should be ✓ or "i".

The game folder is found automatically from the running game. If it is not, set it
in Settings → Game folder (the folder that contains `WorldOfWarships.exe` and
`replays`).

**Sidebar**: **Collapse** at the bottom of the sidebar (or Ctrl+B) shrinks it to the page icons; hover an icon
for its name. Foretop remembers the choice.

## 3. Live Battle

Start the game and enter a battle. Within a second or two Foretop switches to
**Live Battle** and fills both teams:

- name and clan, ship (icon, class, tier), battles, win rate, average damage,
  average XP, Personal Rating, win rate and battles in this ship, last 7 days;
- win rate and battles appear first, then PR and ship stats, then the rest;
- **hover** a row for the full breakdown and where each number comes from;
- **double-click** a player for their profile in Foretop; right-click for the
  official profile or wows-numbers.

**Colours**

| | Red | Yellow | Green | Purple |
|---|---|---|---|---|
| Win rate | < 47 % | 47–53 % | 53–60 % | ≥ 60 % |
| Personal Rating | < 1100 | 1100–1550 | 1550–2100 | ≥ 2100 |
| Damage vs. expected for the ships played | < 85 % | 85–115 % | 115–150 % | ≥ 150 % |

Win rates from fewer than 50 battles (10 in a ship, 5 in the last week) stay grey,
because they say little.

Use **Sort**, **Ship tier** and the **class chips** above the tables to reorder or
filter both teams.

**Statistics filter** (above the tables): count only each player's battles in ships of a minimum tier, so accounts
with thousands of low-tier battles are not flattered by them. Choose **All tiers**, **Tier V+** … **Tier X+** or
**Tier XI only**:

- Battles, win rate, damage, XP and PR are recalculated from the player's official per-ship statistics: the totals
  of every eligible ship are added up, then divided (win rate = wins ÷ battles, average damage = damage ÷ battles,
  and so on; never an average of per-ship averages). PR uses the same formula and expected values, over the same ships.
- **Overall / Filtered / Both** chooses what the columns show; Both puts the filtered value under the overall one.
  The badge next to each team name says which it is (ALL TIERS, TIER VI+ …). Team averages and the strength
  estimate follow the columns.
- Hover a player for both blocks: overall and filtered, with kills, survival and kills/deaths.
- The 7-day column has no per-tier split (it comes from Foretop's own snapshots of the account totals) and shows
  "—" while filtered values are shown. Ships whose tier is unknown (removed from the game) are left out and counted
  in the tooltip.
- Switching is instant and needs no extra requests. **Remember** keeps the choice for the next start; otherwise
  Foretop starts with all tiers.
- Battles recorded before Foretop 1.5.0 have no filtered values ("—"); **Refresh** loads them. **Comparison** shows the teams' averages side by side, their
composition and an estimated strength split. The estimate summarises past
performance; it is not a prediction of the result.

**Open replay…** shows the teams of any `.wowsreplay` file, without the game.

**Refresh** fetches fresh statistics and ignores the cache.

## 4. Player Profiles

Type a name (suggestions appear after three letters) or double-click a player in a
battle. You get overall and ranked statistics, recent activity, most played and
best ships, battles by class and tier, every ship, a win-rate chart and the battles
you recorded with that player.

**Player Search** finds names by their first letters on the server chosen in Settings, or a player by account id
(type the digits, as in a profile link). A player opened from a recorded battle is looked up on that battle's server.

Above the ship list, the **Tier** (I–X, ★) and **Class** (DD, CA, BB, CV, SS) buttons filter it, like the
ship carousel filters in the game: nothing selected shows every ship; select one or more tiers and/or classes
to narrow the list, and the line above the table adds up battles, win rate and average damage for the ships
shown. Greyed-out buttons have no ships for that player; **Clear** resets the filter, which otherwise stays when
you open another player.

**Best performing ships** ranks the player's ships by their Personal Rating (the same per-ship PR as in the ship
list), ten at most. Above it, **All tiers** or any tiers from I to XI (click several, e.g. VI, VII and VIII for a
range) and **Minimum battles** (1+, 5+, 10+, 20+, 50+, 100+ or a custom number; 20+ to start with) choose which
ships take part; both apply together. The tier is the ship's real tier from the ship list. The line under the
filters names the active filters and how many ships match; ships without a PR (no expected values) or with an
unknown tier are never ranked and are counted there. **Reset filters** goes back to all tiers and 20+ battles.
The choice stays while Foretop runs, also when you open another player.

## 5. Battle History and Statistics

Every detected battle (and every replay you open) is saved with everyone's
statistics at that moment.

- **Battle History** lists them with team averages. Double-click to see the full
  rosters again. Select two (Ctrl+click) to compare them.
- **Statistics** summarises your recorded battles: ships, maps, battle types, enemy
  classes, team strength per battle and the players you meet most often.

### Your results (Dashboard)

The Dashboard shows **your own result in each battle**: victory or defeat, damage,
XP, frags and whether you survived, plus totals for **this session** (battles
less than an hour apart) and **today**. The game does not write results anywhere
Foretop can read: the replay's results block stays empty. So Foretop reads your
account totals from the Wargaming API when a battle starts, and again after it ends
until the API has counted it (usually within a minute). The difference is that
battle's result.

- Works for **Random, Ranked and Co-op** battles. The API keeps no totals for
  operations, clan battles, events or training rooms; those show "No result".
- A battle you leave early is counted when it ends for everyone, up to 20 minutes
  later. If two battles are counted at once, they appear together in one row
  ("2 battles"), and the totals stay exact.
- XP is as the API counts it: including Premium Account, flags and other bonuses. Base XP (before them) comes
  from the replay and is shown under Analytics → My performance.
- It needs your account name (Settings → Your account, filled in automatically) and a
  public profile.

### My performance (Analytics)

**Analytics → My performance** analyses your own results; **Recorded battles** is the earlier page about who you met.

**The filter toolbar** at the top stays in view and applies to everything on the page:

- **Source**: **Whole account** = the official per-ship totals of all your Random battles (complete, but without dates
  or battle types); **Recorded battles** = the battles Foretop recorded while it ran (Wargaming offers no battle
  history), one per battle the API counted on its own, with dates, battle types and replay details.
- **Tier**: All tiers, or click tiers (several at once); Shift+click selects a range (click VI, Shift+click VIII =
  VI–VIII); shortcuts VI+, VIII+, X+, VI–VIII, IX–XI. The tier is the ship's real tier from the ship list (game files
  for ships the API list omits); ships whose tier is unknown never match a tier filter.
- **Min. battles** per ship (1+ … 100+, custom): ships with fewer battles in the selection are left out everywhere.
- **Period** (last 7/30/90 days, this/last month, last N battles, date range) and **Type** (Random, Ranked, Co-op):
  recorded battles only.
- **Class**, **Nation**, **Ships** (pick any ships). Active filters show as labels; ✕ removes one, **Clear filters**
  all of them. The line under them says how many battles match.

All filters work together (for example Tier VIII + 20+ battles + battleships + last 90 days). Changing a filter
recalculates from the data already loaded: no API call, no waiting.

Sections (battle-by-battle ones need the recorded source):

- **Last battle** (recorded): date, map, type, ship, result, damage, kills, survival and time survived, XP and base XP,
  spotting and potential damage, hits, team position by damage and a battle rating.
- **Performance in the selection**: battles, victories/defeats/draws, win rate, PR, averages, kills/deaths, survival
  (plus replay and accuracy values for recorded battles).
- **Trends**, **Battle history**, **Compare periods** (recorded): as before; Compare periods uses the toolbar filters
  except the period.
- **Best and worst performing ships**: top 10 by PR, win rate, average damage or battles (best), or lowest PR, win rate
  or damage (worst), ranked from 20+ battles by default so a few unlucky battles do not count as poor.
- **Average damage and win rate per ship**: per ship, tier or class, highest or lowest first, with battles, total
  damage, victories, defeats and draws. Groups add up their battles (never an average of ship win rates).
- **Ship comparison**: tick ships (or "Select all shown", e.g. all Tier VIII ships); a table with battles, win rate, PR,
  average damage and base XP, kills and deaths per battle, K/D and survival, a totals row and a chart.
- **Tier performance**: every tier in one table and chart (the toolbar's tier is highlighted, not applied). Click a
  tier to filter the whole page by it.
- **Compare tiers**: two or three tier groups (e.g. VIII vs X, VI–VIII vs IX–XI, VIII+ vs all), with B − A and the
  relative difference. The groups choose the tiers; every other toolbar filter applies to all groups alike.
- **PR over time** (recorded): PR of all recorded battles up to each point, or of the last 10/20, for several lines at
  once (the selection, all tiers, VI+, VIII+, X+, single tiers). The dashed line is your account PR for the same ships
  and tiers. Calculated by Foretop with the expected values stored with each battle; not an official rating.

Every chart can have its own **chart filter** (class) on top of the toolbar. It can only narrow the selection, never
widen it; when set, the chart says so and **Reset chart filter** clears just that chart.

Every value carries its source:

| Tag | Meaning |
|---|---|
| API | Difference of your official totals before and after the battle (account totals for result, damage, kills, survival, XP, aircraft; the played ship's totals for spotting, potential damage and hits). |
| replay | The replay of the battle: base XP and team position from the game's result data, kills, time survived and battle length from the battle's messages. Needs replays switched on. Foretop checks the result data against the API damage before using it; after a game update that changes its layout those values stay empty until Foretop is updated. |
| calculated | Foretop's arithmetic. **PR** here is the wows-numbers formula on the period's totals, and the **battle rating** of one battle is the same formula on that battle; both use the expected values stored with each battle, so they do not change later. Neither is an official rating. |

Spotting, potential damage and hits are recorded from Foretop 1.6.0 on; older battles show "—" for them.

### Replays

**Replays** in the sidebar lists the replays the game saved (`<game>\replays`, newest first).
Double-click one, or select it and press **Open selected**, to watch the battle on the minimap:

- Ships are arrows pointing where they sail: yours white, allies teal, enemies red, with a
  health bar and the ship name. A hollow arrow is an enemy (or ally) your client could not
  see at that moment, at the last place it was seen; a cross is a sunk ship.
- Capture points carry their letter and turn the owner's colour; grey circles are smoke.
- **Play / Pause** (or Space), ±30 s, 1–40× speed, and the slider to jump anywhere. The big
  clock is the battle clock (time left), the small one the replay time.
- **Zoom**: mouse wheel over the map zooms at the pointer (up to 8×), drag to move, double-click (or **Whole map**,
  or 0) shows the whole map; + and − zoom in and out. While zoomed, a small overview shows which part you see.
- **Full view** (or F; Esc to leave) hides the replay list and the teams so the map gets the room.
- **Debug** draws the map's bounds, the world origin, a grid every 100 units with coordinates and each ship's
  position, plus scale, zoom and camera; for checking that everything is where it should be.
- Right: both teams with health (faded = sunk, dimmer = not in view), the kill feed and the **chat** up to the
  current moment. Click a chat line to jump to it. The latest lines also show at the bottom left of the map, as in
  the game; **Chat** under the map switches that off.

Only what your game client received is shown: enemies appear while someone on your team
had them spotted, as in the battle, and the chat has your team's messages and messages to all, not the enemy team's. Replays need replay recording switched on in the game
and the game installation (Foretop reads the map images and message layout from it).

## 6. Ship Intelligence

Ship Intelligence reads every ship's characteristics, armour and artwork straight
from your installed game client. The first time (and after each game update) it
reads the game files for about ten seconds in the background; the previous data
stays in use until it is done. Nothing in the game folder is changed.

- **Ship Database** lists all 1,246 ships with nation, class, tier, availability
  and key figures. Filter by name, nation, class, tier and availability (the
  default shows the 954 ships you can obtain). Double-click a ship to open its
  profile; tick up to four ships and click **Compare ships**.
- **Test ships** (availability "Test ship": ships the game marks as still being
  tested) show only name, nation, tier and class. Foretop shows no damage,
  characteristics, armour, equipment or statistics for them, anywhere in the app,
  and they cannot be compared.
- **Ship profile**: artwork, description, key figures, general characteristics,
  main armament, shells with penetration by range, turret armour and a summary.
  **Stock / Fully upgraded** switches the configuration.
- **Armour Viewer**: the hull's armour model from the game files, coloured by
  thickness. **Profile + plan** shows the side and the view from above; **Side**,
  **Top**, **Bow** and **3D** show one view (in 3D drag to rotate, Shift-drag to
  move, wheel to zoom, double-click to reset). Switch zones on and off on the left
  (**Citadel only** shows the citadel, barbettes and steering gear on their own).
  Click a plate to see its thickness, zone and position. Turret armour is listed on
  the profile; it is not part of the model yet.
- **Ship Statistics**: your own random-battle results per ship, from the Wargaming
  API. It needs your account name, which Foretop picks up from the first battle it
  sees; you can also enter it in Settings → Your account.
- **Ship Comparison**: up to four ships side by side, any tier, nation and class.
  Bars show each value against the largest in its row; the best value is
  highlighted (for reload, dispersion, detectability, turning circle and rudder
  shift, lower is better).
- **Equipment & Upgrades**: what changes from stock to fully upgraded, every
  module with its research and credit cost, every upgrade the ship can mount per
  slot with the effects the game data gives it, and the consumables.

Values marked **calculated** come from a ballistic model; **theoretical** ones
assume every shell hits and deals full damage. Neither is how players actually do;
that is under Ship Statistics and Player Profiles.

The top-bar search also finds ships: type a ship's name and press Enter.

## 7. Updates

Foretop looks for a new version on GitHub about 20 seconds after it starts and then once a day (Settings →
About → Updates; **Check now** checks at once). When there is one, an **Update x.y.z** button appears in the top
bar. It opens the release notes with:

- **Install and restart** (installed version): downloads the installer, checks it against GitHub's SHA-256,
  closes Foretop, installs the update with a progress bar and starts Foretop again. Settings and history stay.
- **Open download page** (portable version): download the new zip and replace `Foretop.exe`.
- **Skip this version** hides the button until a newer one comes out; **Later** just closes the panel.

Downloaded installers are kept in `%LOCALAPPDATA%\Foretop\updates`. The check only reads the public release page
and sends nothing about you; it needs the GitHub repository to be public.

## 8. Where the numbers come from

- **Official** — battles, wins, damage, XP, frags, survival, planes: straight from
  the Wargaming API.
- **Derived** — averages and rates: simple arithmetic on official numbers.
- **Third-party** — Personal Rating and damage vs. expected: based on the expected
  values published by wows-numbers.com.
- **Recent (7 / 28 days)** — Wargaming offers no recent statistics any more, so
  Foretop compares its own snapshots of a player. A player's recent numbers appear
  once Foretop has seen them at least 7 days earlier.

## 9. Settings reference

| Setting | Meaning |
|---|---|
| Server region | Europe, North America or Asia. Must match the server you play on. |
| Application ID | Your Wargaming ID; stored encrypted for your Windows account. |
| Requests per second | Speed limit towards Wargaming (they allow about 10). |
| Game folder | Empty = automatic. |
| Your account | Your in-game name; filled in from the first battle. Used for Ship Statistics and your results. |
| Detect battles automatically | Turn the watcher off without closing the app. |
| Switch to Live Battle | Jump to the battle when one starts. |
| Show bots | Show AI ships in co-op and operations. |
| Theme | System (follows Windows), Dark, Light, Steel (refined dark, higher contrast), Chart Room (light, paper and navy ink) or Combat Information Center (near-black, dense, blue vs orange teams). A theme sets colours, corners, fonts and row height; a font chosen below overrides its fonts. |
| Font | Any font installed on Windows (the list shows each in its own style); Default is Segoe UI. |
| Text size | 90–150 %. Scales the whole interface, so tables and charts keep their proportions. |
| Check for updates | Look for a new version on GitHub after start and once a day (About → Updates). |
| Compact mode | Fewer columns, tighter rows. |
| Start with Windows / minimised | Launch on sign-in. |
| Cache times | How long statistics are reused before fetching again. |

## 10. Troubleshooting

Run **Settings → Run check** first; it names the problem in most cases.

| Symptom | What to do |
|---|---|
| No battle appears | Replays enabled in the game? Game folder found? The check shows both. |
| "Game is running, but Foretop may not read its path" | The game runs as administrator. Set the game folder manually, or run Foretop the same way. |
| "No Wargaming application ID" / "rejected the application ID" | Enter or re-check the ID in Settings, then Test. |
| Everyone shows "Not found" | The server region does not match where you play. |
| "offline data" on rows | Wargaming's API was unreachable; you see the last cached statistics. |
| Rows show "Hidden profile" | That player hid their statistics; Wargaming shares nothing else. |
| Recent / 7-day columns empty | Normal at first: they fill in as Foretop sees players again over the days. |
| Files left in `%LOCALAPPDATA%\Programs\Foretop` after uninstalling | The exe was in use (often a virus scan right after running it). Delete the folder, or it goes after the next restart. |
| Something else | Settings → About → *Open log folder*, and look at the newest `foretop-*.log`. |

## 11. Privacy and fair play

Foretop reads one file the game itself writes for replays. It does not read game
memory, inject code, capture network traffic or automate anything. It talks only to
the Wargaming API (player statistics), wows-numbers.com (PR expected values) and GitHub (the update check, which
only reads the public release page).

Foretop is not affiliated with or endorsed by Wargaming. World of Warships is a
trademark of Wargaming.
