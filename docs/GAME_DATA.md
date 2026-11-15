# Game-client data: verified findings (build 13533378, 2026-11-14)

What Foretop reads from the installed client (`F:\Wows` on the development PC) for Ship Intelligence, checked against
real files. Format sources: wows-toolkit @ 857a94d, WoWs-Builder-Team/DataConverter, jcw780/wows_shell. Everything is
read-only; nothing in the game folder is changed.

## In code

| File (`src/Foretop.Core/GameData/`) | What it does |
|---|---|
| `GameFileSystem.cs` | All `bin\<build>\idx\*.idx` ("ISFP" v0x02000000, LE) and `res_packages\*.pkg` (raw deflate). 393,556 files indexed in about 0.5 s. |
| `Unpickler.cs` | Safe pickle reader (protocols 0–5, `copy_reg._reconstructor`). Builds no arbitrary objects. |
| `GameParamsReader.cs` | `content/GameParams.data` → reverse bytes → zlib → unpickle. 24,496 entities in 4.3 s, about 950 MB peak. |
| `GameTexts.cs` | `global.mo` of the client language (UTF-8, no charset header). |
| `ShipExtractor.cs` | 1,246 ship records (stock and fully upgraded configuration, modules, shells, AA, torpedoes, consumables, upgrades, armour plates). |
| `ShipDataStore.cs` | Runs the extractor once per build in a helper process (`Foretop.exe --extract-ship-data`, about 11 s, below-normal priority), keeps `%LOCALAPPDATA%\Foretop\shipdata\<build>.json.br` (≈ 0.9 MB) and the artwork, and reads armour models on demand. |
| `ArmourGeometry.cs` | Armour triangles from the hull's `.geometry` files. |
| `Ballistics.cs` | Penetration, flight time and angle of fall by range (RK4, dt 0.02 s). |
| `ArmourMaterials.cs` | The 255 material names (wows-toolkit `armor_materials.rs`). |

`tools/Foretop.Probe` (dev console): `vfs`, `ls <prefix>`, `extract <path> <out>`, `gp <name> <out.json>`,
`dump <outdir> <names…>`, `type <TypeName> <out.json>`, `ships <out.json>`, `armour <hull .model>`, `field <entity> <key>`.
Run as `dotnet run --project tools/Foretop.Probe -- <game folder> <command> …`.

The cache carries `ShipExtractor.FormatVersion` (now 5); a cache from an older format is rebuilt automatically.

## Test ships

GameParams `group` `demoWithoutStats` (21 ships) and `demoWithoutStatsPrem` (31) are ships in testing, e.g. Vladimir
Monomakh, Cumberland, Montana 2, Kléber TE. The game shows no statistics for them. Foretop extracts only their identity
(name, nation, tier, class, description, artwork): no configuration, modules, consumables, upgrades, armour or hull
model (format 5). The app shows them as "Test ship" in the list, with a notice instead of profile, armour and equipment;
they cannot be compared; per-ship player results for them are dropped (`PlayerStatsService.IsTestShip`), so Live
Battle, profiles, Ship Statistics and PR never use them; the Dashboard hides damage/XP/frags of a battle in one and
leaves it out of the totals; Replays show no hit points for them.

## Verified against the design's Yamato values

| Value | Source | Result |
|---|---|---|
| 1,246 ships, 954 obtainable | `typeinfo.type == "Ship"`; `group` → availability | ✓ |
| HP 97,200, 27 kn, 900 m turn | `A_Hull.health / maxSpeed / turningRadius` | ✓ |
| Rudder 22.1 s | `A_Hull.rudderTime` 28.78 × 2 / 2.61 | ✓ |
| Detectability 17.46 / 10 / 19.27 km | `visibilityFactor / ByPlane / visibilityCoefGKInSmoke` | ✓ |
| Range 26.63 km, secondary 7.3 km | `A_Artillery.maxDist` × FC `maxDistCoef`; `A_ATBA.maxDist` (m) | ✓ |
| Reload 30 s, 180° 60 s, sigma 2.1 | gun `shotDelay`, 180 / `rotationSpeed[0]`, `sigmaCount` | ✓ |
| Dispersion 276 m | (minR + km/30·1000·(idealR−minR)/idealDistance) **× 30** (not × 15) | ✓ |
| 55 plates, 16–560 mm | `A_Hull.armor`: 71 keys, 55 non-zero | ✓ |
| Turret 650/270/250/190/135 | gun `armor`, key = (layer<<16)\|material | ✓ |
| AA 389 / 147 per s, 5.8 km; flak 6 × 1,610 | aura `areaDamage / areaDamagePeriod`; (inner + outer bubbles) × bubbleDamage × 7 | ✓ |
| Penetration table at 5–26.63 km | `Ballistics` against the design's table (unit tests) | ✓ |
| 6 upgrade slots, 21 options | applicability rules below | ✓ (Shimakaze: 23, design 24 — open) |
| Belt 80–193 m from the bow, 2.3 m below to 3.1 m above the waterline | armour model, key 65597 (`Cit_Belt`, layer 1) | ✓ |

## Ship ids and names

- `id` in GameParams is the Wargaming API `ship_id`. All 1,026 ships in the API encyclopedia match a game record.
- Names: `IDS_<index>_FULL` first (e.g. "Admiral Orlov"), then `IDS_<index>` (the short label "Orlov"). With `_FULL`
  every API name matches the game name exactly.
- Pitfall fixed on 2026-11-14: a `cond ? (long)big : big` in the unpickler made every pickled long a `BigInteger`,
  so `id` read as 0. Covered by a unit test now.

## Upgrades

- Applicability: slot −1 = retired; a ship in `excludes` never gets it; in `ships` always; otherwise the ship's group
  must be in `group`, type 3 (unique) only goes to listed ships, an upgrade without tier/class/nation criteria only to
  listed ships, and type 1 (consumable upgrade) needs the matching consumable on board.
- "Unique" = type 3. The `ships` list is also used by ordinary upgrades, so it says nothing about uniqueness.
- Effect labels: curated table first, then the game's own `IDS_PARAMS_MODIFIER_<KEY>_SKILL` / `IDS_PARAMS_MODIFIER_<KEY>`.
  Keys ending in `Coeff`, `Coef`, `Multiplier` and `vulnerability*` are factors (shown as ±%); a few are counts or
  metres. A modifier that changes nothing, or has no readable label anywhere (engine-acceleration internals such as
  `engineForwardForsageMaxSpeed`), is left out rather than shown under its internal name.

## Armour model (`.geometry`)

- The hull model's folder (`A_Hull.model`, e.g. `content/gameplay/japan/ship/battleship/JSB039_Yamato_1945/`) holds a
  main `.geometry` and section files (`_Bow`, `_MidFront`, `_MidBack`, `_Stern`). Only the main file carries the
  armour model; files with "dead" in the name are skipped.
- Header: `armorModelCount` at 0x14, `armorModelsPtr` at 0x40. Each 32-byte prototype: blob size (u64), name, data
  pointer (relative to the prototype). Blob: two 16-byte header entries, then nodes of two header entries
  (`(layer<<16)|material` at +0, vertex count at +28) followed by 16-byte vertices; every three vertices form a triangle.
- Model space is left-handed (X beam, Y up, Z forward), 1 unit = 15 m. Foretop converts to metres with X = length
  (bow positive), Y = height (0 = waterline), Z = beam.
- Yamato: 6,978 triangles, 263.0 m long, 40.8 m beam, 10.4 m below the waterline. Read in about 80 ms.
- Keys without a thickness in `A_Hull.armor` (15 on Yamato) are section boundaries; the viewer hides them by default.
- Turret and other mount armour needs the hardpoint transforms in `content/assets.bin`; not read yet, so turret armour
  is a table on the profile, not part of the model.

## Texts (`bin\<build>\res\texts\<lang>\LC_MESSAGES\global.mo`)

`IDS_<index>` short name, `_FULL`, `_DESCR`, `_YEAR` (commissioned); modules/guns/shells `IDS_<NAME_UPPER>`;
consumables `IDS_DOCK_CONSUME_TITLE_<NAME_UPPER>`; upgrades `IDS_TITLE_/IDS_DESC_<NAME_UPPER>`; armour plates
`IDS_<MATERIAL_UPPER>` (e.g. `IDS_CIT_BELT` = "Armor Belt"); modifiers `IDS_PARAMS_MODIFIER_<KEY_UPPER>`.

Maps: `IDS_SPACES/<SPACE_UPPER>` (87 in build 13533378), keyed by `tempArenaInfo.json`'s `mapDisplayName`.
The internal names often differ completely: `28_naval_mission` = "Tears of the Desert", `45_Zigzag` = "Loop",
`38_Canada` = "Shatter". Stored in the ship-data cache (`Maps`, format 4) and used for every map name in the app.

## Replays (`.wowsreplay`)

Verified on a build 13533378 Random battle (Tears of the Desert, 822 s, 24 players).

- File: magic `0x11343212`, block count, length-prefixed blocks (0 = arena JSON, 1 = results, empty here,
  2 = `"<account id>.<arena id>"`), then uint32 unpacked size, uint32 packed size, and the packed stream:
  Blowfish-ECB, key `29 B7 C9 09 38 3F 84 88 FA 98 EC 4E 13 19 79 FB`, each block XOR-ed with the previous plain block,
  then zlib.
- Packets: uint32 size, uint32 type, float32 time (s), payload. Types used: `0x00` BasePlayerCreate (avatar id),
  `0x04` EntityLeave, `0x05` EntityCreate (id i32, type u16 1-based in `entities.xml` order, vehicle i32, space i32,
  position, direction, then u32 length + state: u8 count, (u8 property index, value)…), `0x07` EntityProperty and
  `0x08` EntityMethod (id, index, u32 length, value), `0x0A` Position (id, vehicle, position, error, yaw/pitch/roll,
  flag; 45 bytes), `0x2C` PlayerPosition (id, 0, position, yaw/pitch/roll; 32 bytes), `0x22` battle results (JSON).
- Indices: client methods and client-visible properties (`ALL_CLIENTS`, `OTHER_CLIENTS`, `OWN_CLIENT`,
  `CELL_PUBLIC_AND_OWN`, `BASE_AND_CLIENT`), parent and interfaces first, then own, stable-sorted by encoded size with
  anything variable-length last. Checked: `Vehicle.health` = 28, `regenerationHealth` = 29, `ruddersAngle` = 40,
  `visibilityTime` = 42, `Avatar.onArenaStateReceived` = 154 all match the packets. Own-first ordering gives 37 and 149.
- Values: strings, blobs and open arrays have a u8 length, or 0xFF and a 24-bit length; nullable `FIXED_DICT` has a
  u8 flag first.
- Yaw: 0 = north, clockwise positive. World X east, Z north; the minimap square is ±(playable half), north up.
- `InteractiveZone.componentsState.controlPoint.index` 0/1/2 = A/B/C; `captureLogic` holds progress and capture time.
- There are no game texts for death reasons, so the player shows who sank whom, not the weapon.
- Battle results (packet 0x22, written at the end): JSON with `privateDataList` (own credits, XP, missions…),
  `commonList` (arena id, duration, map, mode) and `playersPublicInfo` = account id → 550 positional values per player.
  The game's scripts that name the positions are protected, so positions are identified from known values. Checked on
  build 13533378 against the API and the replay's own messages: 0 = account id, 7 = ship id, 416 = base XP (equals the
  "XP" of daily missions; the API's XP is it × 1.65 = Premium Account), 438 = damage (equals the API difference),
  420/421 = killer's account and vehicle id. Foretop uses 416 and 438 only when 0, 7 and the damage check hold.
  Kills, survival and time survived come from `receiveVehicleDeath` and the battle timer instead (battle start = the
  timer jumping from the countdown to the battle length; duration 791 s = 1,200 − 409 left at the end).
- Chat: `Avatar.onChatMessage` (index 152) with `PLAYER_ID` sender, then three unnamed strings: channel
  (`battle_team`, `battle_common`), the text, and one more (empty so far). The sender is neither the account id nor the
  vehicle id: it is a third id in the `onArenaStateReceived` player records, found by matching the senders' values
  (checked: 7 lines from 3 allies). Only lines the recording client received are in the replay, so enemy team chat is
  never there. `receiveChatHistory` (a blob, for reconnects) is not read.

## Open

- Shimakaze lists 23 upgrade options, the design 24.
- Turret armour in the 3D model (needs `assets.bin` hardpoints).
- Section cut in the armour viewer (in the design, not built).
