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

Use **Sort**, **Tier** and the **class chips** above the tables to reorder or
filter both teams. **Comparison** shows the teams' averages side by side, their
composition and an estimated strength split. The estimate summarises past
performance; it is not a prediction of the result.

**Open replay…** shows the teams of any `.wowsreplay` file, without the game.

**Refresh** fetches fresh statistics and ignores the cache.

## 4. Player Profiles

Type a name (suggestions appear after three letters) or double-click a player in a
battle. You get overall and ranked statistics, recent activity, most played and
best ships, battles by class and tier, every ship, a win-rate chart and the battles
you recorded with that player.

## 5. Battle History and Statistics

Every detected battle (and every replay you open) is saved with everyone's
statistics at that moment.

- **Battle History** lists them with team averages. Double-click to see the full
  rosters again. Select two (Ctrl+click) to compare them.
- **Statistics** summarises your recorded battles: ships, maps, battle types, enemy
  classes, team strength per battle and the players you meet most often.

### Your results (Dashboard)

The Dashboard shows **your own result in each battle**: victory or defeat, damage,
base XP, frags and whether you survived, plus totals for **this session** (battles
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
- XP is base XP (before premium time, flags and bonuses), as the API reports it.
- It needs your account name (Settings → Your account, filled in automatically) and a
  public profile.

### Replays

**Replays** in the sidebar lists the replays the game saved (`<game>\replays`, newest first).
Double-click one, or select it and press **Open selected**, to watch the battle on the minimap:

- Ships are arrows pointing where they sail: yours white, allies teal, enemies red, with a
  health bar and the ship name. A hollow arrow is an enemy (or ally) your client could not
  see at that moment, at the last place it was seen; a cross is a sunk ship.
- Capture points carry their letter and turn the owner's colour; grey circles are smoke.
- **Play / Pause** (or Space), ±30 s, 1–40× speed, and the slider to jump anywhere. The big
  clock is the battle clock (time left), the small one the replay time.
- Right: both teams with health (faded = sunk, dimmer = not in view) and the kill feed.

Only what your game client received is shown: enemies appear while someone on your team
had them spotted, as in the battle. Replays need replay recording switched on in the game
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

## 7. Where the numbers come from

- **Official** — battles, wins, damage, XP, frags, survival, planes: straight from
  the Wargaming API.
- **Derived** — averages and rates: simple arithmetic on official numbers.
- **Third-party** — Personal Rating and damage vs. expected: based on the expected
  values published by wows-numbers.com.
- **Recent (7 / 28 days)** — Wargaming offers no recent statistics any more, so
  Foretop compares its own snapshots of a player. A player's recent numbers appear
  once Foretop has seen them at least 7 days earlier.

## 8. Settings reference

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
| Theme | System (follows Windows), Dark or Light. |
| Compact mode | Fewer columns, tighter rows. |
| Start with Windows / minimised | Launch on sign-in. |
| Cache times | How long statistics are reused before fetching again. |

## 9. Troubleshooting

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

## 10. Privacy and fair play

Foretop reads one file the game itself writes for replays. It does not read game
memory, inject code, capture network traffic or automate anything. It talks only to
the Wargaming API (player statistics) and wows-numbers.com (PR expected values).

Foretop is not affiliated with or endorsed by Wargaming. World of Warships is a
trademark of Wargaming.
