# Real-game test checklist

The one thing not yet tested is a real World of Warships client (the development PC
has no game). Run this once on the PC you play on. It takes about three battles.

## Already verified without the game

- Live Wargaming API (EU) with a real application ID; values match the raw API for
  sampled players. The same ID works on NA and Asia.
- A real 22-player roster: 23 requests and 3.2 s with an empty cache, 0.1 s cached.
- End-to-end detection with a stand-in process named `WorldOfWarships64.exe` in a fake
  install folder: battle noticed about 1 s after the file was written, app switched to
  Live Battle by itself, battle end detected when the process exited.
- MSI install, run, uninstall (user data kept) and reinstall on Windows 11.
- 58 unit tests (parser, PR, scheduler, cache, offline fallback, history, diagnostics).

## On the gaming PC

Install (MSI or portable zip), add the application ID, then:

| # | Step | Expected | ✓ |
|---|---|---|---|
| 1 | Settings → **Run check** with the game closed | Game folder ✓ (found automatically), Replays ✓, API ✓ | |
| 2 | Start the game, stay in port, Run check again | Game running ✓; Battle file "i" | |
| 3 | Enter a **Random battle** | Within ~2 s: Live Battle, LIVE badge, map and both teams listed | |
| 4 | Look at your own row | Bold name, your ship, highlighted row | |
| 5 | Wait ~5 s | Win rates, PR and ship stats filled for everyone except hidden profiles | |
| 6 | Pick 2–3 players, compare with their profile on wows-numbers or the official site | Battles, win rate, damage match (PR within a few points — expected values update daily) | |
| 7 | Battle ends, back in port | LIVE badge goes away; table stays until the next battle | |
| 8 | Next battle | Table replaced by the new battle | |
| 9 | **Co-op** or an operation, if you play them | Bots marked "Bot"; humans loaded | |
| 10 | Ranked or clan battle, if available | Title shows the battle type | |
| 11 | Battle History | The battles appear; double-click shows the roster | |
| 12 | Leave Foretop running for a session | No slowdown; Task Manager shows roughly 0 % CPU while idle | |

## If something fails

Note the step, then send:

- `%LOCALAPPDATA%\Foretop\logs\foretop-<date>.log` (no secrets in it: the
  application ID is never logged);
- the output of **Run check**;
- if possible a copy of `<game>\replays\tempArenaInfo.json` taken during a battle
  (only player names and ship IDs).

Things most likely to differ on a real client, and what Foretop does about them:

- **File in a version subfolder** (`replays\<version>\`): searched as well.
- **Game started as administrator**: the path cannot be read; set the folder manually.
- **A client that never writes the file**: the check shows "No tempArenaInfo.json"
  during a battle; replays are then probably off.
