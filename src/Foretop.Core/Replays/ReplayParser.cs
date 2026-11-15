using System.Globalization;
using System.Text.Json;
using Foretop.Core.GameData;

namespace Foretop.Core.Replays;

/// <summary>
/// Turns a replay's packet stream into what the minimap player shows: ship tracks, when each ship was visible, health,
/// kills, capture points, smoke and the battle clock. Packets: uint32 size, uint32 type, float32 time, payload.
/// Entity messages are decoded with <see cref="EntityDefs"/>; packet type numbers are the ones of client 15.x
/// (checked against build 13533378) and unknown types are skipped.
/// </summary>
public static class ReplayParser
{
    private const uint BasePlayerCreate = 0x00;
    private const uint EntityLeave = 0x04;
    private const uint EntityCreate = 0x05;
    private const uint EntityProperty = 0x07;
    private const uint EntityMethod = 0x08;
    private const uint Position = 0x0A;
    private const uint PlayerPosition = 0x2C;

    public static ReplayData Parse(ReplayFile file, EntityDefs defs)
    {
        using var arena = JsonDocument.Parse(file.ArenaJson);
        var root = arena.RootElement;
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var warnings = new List<string>();
        var vehicleDef = defs.Entity("Vehicle");
        var avatarDef = defs.Entity("Avatar");
        var zoneDef = defs.Entity("InteractiveZone");
        var smokeDef = defs.Entity("SmokeScreen");
        var logicDef = defs.Entity("BattleLogic");
        if (vehicleDef is null || avatarDef is null) throw new InvalidDataException("The game's entity definitions could not be read.");

        var iHealth = vehicleDef.PropertyIndex("health");
        var iMaxHealth = vehicleDef.PropertyIndex("maxHealth");
        var mDeath = avatarDef.MethodIndex("receiveVehicleDeath");
        var mArena = avatarDef.MethodIndex("onArenaStateReceived");
        var mGameRoom = avatarDef.MethodIndex("onGameRoomStateChanged");
        var mSpawned = avatarDef.MethodIndex("onNewPlayerSpawnedInBattle");

        var types = new Dictionary<int, string>();
        var tracks = new Dictionary<int, List<TrackPoint>>();
        var seen = new Dictionary<int, List<Visibility>>();
        var enteredAt = new Dictionary<int, float>();
        var health = new Dictionary<int, List<ValuePoint>>();
        var maxHealth = new Dictionary<int, float>();
        var teams = new Dictionary<int, int>();
        var deaths = new List<ReplayDeath>();
        var zones = new Dictionary<int, ZoneBuilder>();
        var smokes = new Dictionary<int, SmokeBuilder>();
        var timeLeft = new List<ValuePoint>();
        var playerBlobs = new List<byte[]>();
        var avatarId = -1;
        float last = 0;
        var failures = new Dictionary<string, int>();

        var pk = file.Packets;
        var p = 0;
        while (p + 12 <= pk.Length)
        {
            var size = BitConverter.ToInt32(pk, p);
            var type = BitConverter.ToUInt32(pk, p + 4);
            var t = BitConverter.ToSingle(pk, p + 8);
            var body = p + 12;
            if (size < 0 || body + size > pk.Length) { warnings.Add("The replay ends in a cut-off packet."); break; }
            var payload = pk.AsSpan(body, size);
            p = body + size;
            if (t > last && t < 100_000) last = t;

            try
            {
                switch (type)
                {
                    case BasePlayerCreate when size >= 6:
                        avatarId = BitConverter.ToInt32(payload);
                        types[avatarId] = "Avatar";
                        break;

                    case EntityCreate when size >= 42:
                    {
                        var id = BitConverter.ToInt32(payload);
                        var typeName = defs.TypeName(BitConverter.ToInt16(payload[4..]));
                        if (typeName is null) break;
                        types[id] = typeName;
                        var x = BitConverter.ToSingle(payload[14..]);
                        var z = BitConverter.ToSingle(payload[22..]);
                        var stateLen = BitConverter.ToInt32(payload[38..]);
                        var state = payload.Slice(42, Math.Min(stateLen, size - 42));
                        var def = defs.Entity(typeName);
                        var values = def is null ? [] : ReadState(state, def, defs);
                        switch (typeName)
                        {
                            case "Vehicle":
                                enteredAt[id] = t;
                                if (values.TryGetValue("teamId", out var team) && team is long tid) teams[id] = (int)tid;
                                if (values.TryGetValue("maxHealth", out var mh) && mh is double m) maxHealth[id] = (float)m;
                                if (values.TryGetValue("health", out var hv) && hv is double h) Add(health, id, new ValuePoint(t, (float)h));
                                break;
                            case "InteractiveZone":
                                zones[id] = new ZoneBuilder(id, x, z, t)
                                {
                                    Index = values.GetValueOrDefault("componentsState") is Dictionary<string, object?> cs
                                            && cs.GetValueOrDefault("controlPoint") is Dictionary<string, object?> cp && cp.GetValueOrDefault("index") is long ix
                                        ? (int)ix : -1,
                                    Radius = values.GetValueOrDefault("radius") is double r ? (float)r : 0,
                                    Name = values.GetValueOrDefault("name") as string ?? "",
                                    Kind = values.GetValueOrDefault("type") is long k ? (int)k : -1,
                                };
                                zones[id].Owners.Add((t, values.GetValueOrDefault("teamId") is long tm ? (int)tm : -1));
                                break;
                            case "SmokeScreen":
                                smokes[id] = new SmokeBuilder(id, t)
                                {
                                    Radius = values.GetValueOrDefault("radius") is double sr ? (float)sr : 0,
                                    Points = Points(values.GetValueOrDefault("points")),
                                };
                                break;
                            case "BattleLogic":
                                if (values.GetValueOrDefault("timeLeft") is long tl) timeLeft.Add(new ValuePoint(t, tl));
                                break;
                        }
                        break;
                    }

                    case EntityLeave when size >= 4:
                    {
                        var id = BitConverter.ToInt32(payload);
                        if (enteredAt.Remove(id, out var from)) Add(seen, id, new Visibility(from, t));
                        if (smokes.TryGetValue(id, out var sm) && sm.To is null) sm.To = t;
                        if (zones.TryGetValue(id, out var zn) && zn.To is null) zn.To = t;
                        break;
                    }

                    case Position when size >= 45:
                    {
                        var id = BitConverter.ToInt32(payload);
                        Add(tracks, id, new TrackPoint(t, BitConverter.ToSingle(payload[8..]), BitConverter.ToSingle(payload[16..]), BitConverter.ToSingle(payload[32..])));
                        break;
                    }

                    case PlayerPosition when size >= 32:
                    {
                        var id = BitConverter.ToInt32(payload);
                        var other = BitConverter.ToInt32(payload[4..]);
                        float x = BitConverter.ToSingle(payload[8..]), z = BitConverter.ToSingle(payload[16..]);
                        if (other != 0 || (x == 0 && z == 0)) break;
                        Add(tracks, id, new TrackPoint(t, x, z, BitConverter.ToSingle(payload[20..])));
                        break;
                    }

                    case EntityProperty when size >= 12:
                    {
                        var id = BitConverter.ToInt32(payload);
                        var index = BitConverter.ToInt32(payload[4..]);
                        var data = payload[12..];
                        if (!types.TryGetValue(id, out var typeName)) break;
                        if (typeName == "Vehicle")
                        {
                            if (index == iHealth && data.Length >= 4) Add(health, id, new ValuePoint(t, BitConverter.ToSingle(data)));
                            else if (index == iMaxHealth && data.Length >= 4) maxHealth[id] = BitConverter.ToSingle(data);
                        }
                        else if (typeName == "InteractiveZone" && zoneDef is not null && zones.TryGetValue(id, out var zone))
                        {
                            var prop = Prop(zoneDef, index);
                            if (prop?.Name == "teamId") zone.Owners.Add((t, (int)(long)new BigWorldReader(data, defs).Read(prop.Type)!));
                            else if (prop?.Name == "radius") zone.Radius = (float)(double)new BigWorldReader(data, defs).Read(prop.Type)!;
                        }
                        else if (typeName == "SmokeScreen" && smokeDef is not null && smokes.TryGetValue(id, out var smoke))
                        {
                            var prop = Prop(smokeDef, index);
                            if (prop?.Name == "points") smoke.Points = Points(new BigWorldReader(data, defs).Read(prop.Type));
                            else if (prop?.Name == "radius") smoke.Radius = (float)(double)new BigWorldReader(data, defs).Read(prop.Type)!;
                        }
                        else if (typeName == "BattleLogic" && logicDef is not null && Prop(logicDef, index)?.Name == "timeLeft")
                        {
                            timeLeft.Add(new ValuePoint(t, (long)new BigWorldReader(data, defs).Read(Prop(logicDef, index)!.Type)!));
                        }
                        break;
                    }

                    case EntityMethod when size >= 12:
                    {
                        var id = BitConverter.ToInt32(payload);
                        if (id != avatarId) break;
                        var index = BitConverter.ToInt32(payload[4..]);
                        var data = payload[12..];
                        if (index == mDeath && data.Length >= 12)
                            deaths.Add(new ReplayDeath(t, BitConverter.ToInt32(data), BitConverter.ToInt32(data[4..]), BitConverter.ToInt32(data[8..])));
                        else if (index == mArena || index == mGameRoom || index == mSpawned)
                        {
                            var r = new BigWorldReader(data, defs);
                            foreach (var arg in avatarDef.ClientMethods[index].Args)
                            {
                                var v = r.Read(arg.Type);
                                if (arg.Name is "playersStates" or "playersData" && v is byte[] blob) playerBlobs.Add(blob);
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException or InvalidCastException or NullReferenceException)
            {
                var key = $"0x{type:X2}: {ex.Message}";
                failures[key] = failures.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var (id, from) in enteredAt) Add(seen, id, new Visibility(from, last));
        foreach (var (k, n) in failures.OrderByDescending(f => f.Value).Take(5)) warnings.Add($"{n} packet(s) not decoded ({k})");

        var vehicleIds = types.Where(kv => kv.Value == "Vehicle").Select(kv => kv.Key).ToHashSet();
        long? selfAccount = file.ExtraBlocks.Select(b => b.Split('.')[0].Trim()).Select(s => long.TryParse(s, out var a) ? a : (long?)null).FirstOrDefault(a => a > 0);
        var players = BuildPlayers(root, playerBlobs, vehicleIds, selfAccount, warnings);

        // The recording player's ship is in view the whole time.
        if (players.FirstOrDefault(pl => pl.IsSelf) is { VehicleId: > 0 } self)
            seen[self.VehicleId] = [new Visibility(0, last)];
        foreach (var list in tracks.Values) list.Sort((a, b) => a.T.CompareTo(b.T));

        DateTime? started = DateTime.TryParseExact(S("dateTime"), "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        return new ReplayData
        {
            MapSpace = S("mapName"),
            MapDisplayName = S("mapDisplayName"),
            PlayerName = S("playerName"),
            PlayerVehicle = S("playerVehicle"),
            MatchGroup = S("matchGroup"),
            Scenario = S("scenario"),
            StartedAt = started,
            ClientVersion = S("clientVersionFromExe"),
            Duration = last,
            Players = players,
            SelfTeam = players.FirstOrDefault(pl => pl.IsSelf) is { } me && teams.TryGetValue(me.VehicleId, out var st) ? st
                : players.Where(pl => pl.Relation == 1).Select(pl => teams.TryGetValue(pl.VehicleId, out var at) ? at : -1).FirstOrDefault(x => x >= 0, -1),
            Tracks = tracks,
            Seen = seen,
            Health = health,
            MaxHealth = maxHealth,
            Deaths = deaths,
            Zones = zones.Values.Where(z => z.Radius > 0)
                .Select(z => new ReplayZone(z.Id, z.X, z.Z, z.Radius, z.Index >= 0 && z.Index < 26 ? ((char)('A' + z.Index)).ToString() : "",
                    z.Owners, z.From, z.To ?? last)).ToList(),
            Smokes = smokes.Values.Where(s => s.Points.Count > 0).Select(s => new ReplaySmoke(s.Id, s.From, s.To ?? last, s.Radius, s.Points)).ToList(),
            TimeLeft = timeLeft,
            Warnings = warnings,
        };
    }

    private static PropertyDef? Prop(EntityDef def, int index) => index >= 0 && index < def.Properties.Count ? def.Properties[index] : null;

    private static void Add<T>(Dictionary<int, List<T>> map, int id, T value)
    {
        if (!map.TryGetValue(id, out var list)) map[id] = list = [];
        list.Add(value);
    }

    /// <summary>Entity-create state: a count, then (property index, value) pairs. Stops quietly at a type it cannot read.</summary>
    private static Dictionary<string, object?> ReadState(ReadOnlySpan<byte> state, EntityDef def, EntityDefs defs)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state.Length == 0) return values;
        var r = new BigWorldReader(state, defs);
        try
        {
            var count = r.U8();
            for (var i = 0; i < count && r.Remaining > 0; i++)
            {
                var prop = Prop(def, r.U8());
                if (prop is null) break;
                values[prop.Name] = r.Read(prop.Type);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException)
        {
            // Keep what was read before the unknown type.
        }
        return values;
    }

    private static List<(float X, float Z)> Points(object? value) =>
        value is List<object?> list ? list.OfType<float[]>().Where(v => v.Length >= 3).Select(v => (v[0], v[2])).ToList() : [];

    /// <summary>
    /// Players from the arena info (name, ship, relation), linked to their vehicle entity through the pickled player
    /// list of <c>onArenaStateReceived</c>. Its records are (key index, value) pairs whose key numbering changes
    /// between versions, so the keys are found by their values: the player's name and the vehicle's entity id.
    /// </summary>
    private static List<ReplayPlayer> BuildPlayers(JsonElement root, List<byte[]> blobs, HashSet<int> vehicleIds, long? selfAccount, List<string> warnings)
    {
        var arena = new List<(string Name, long ShipId, int Relation)>();
        if (root.TryGetProperty("vehicles", out var vs))
            foreach (var v in vs.EnumerateArray())
                arena.Add((v.GetProperty("name").GetString() ?? "", v.GetProperty("shipId").GetInt64(), v.GetProperty("relation").GetInt32()));
        var names = arena.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        var records = new List<Dictionary<long, object?>>();
        foreach (var blob in blobs)
        {
            object? obj;
            try { obj = Unpickler.Load(blob); }
            catch (Exception) { warnings.Add("The player list in the replay could not be read."); continue; }
            if (obj is not IList<object?> list) continue;
            foreach (var entry in list)
            {
                if (entry is not IEnumerable<object?> pairs) continue;
                var rec = new Dictionary<long, object?>();
                foreach (var pair in pairs)
                    if (pair is IList<object?> { Count: 2 } kv && kv[0] is long key) rec[key] = kv[1];
                if (rec.Count > 0) records.Add(rec);
            }
        }

        long? Vote(Func<object?, bool> match) => records.SelectMany(r => r.Where(kv => match(kv.Value)).Select(kv => kv.Key))
            .GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => (long?)g.Key).FirstOrDefault();
        var nameKey = Vote(v => v is string s && names.Contains(s));
        var vehicleKey = Vote(v => v is long l && vehicleIds.Contains((int)l));
        // Account id: a large integer that is the same kind for every record and is not the vehicle id.
        // Account id: the key whose value in the recording player's record is the id in header block 2 ("<account>.<arena>").
        long? accountKey = null;
        var selfName = root.TryGetProperty("playerName", out var pn) ? pn.GetString() : null;
        if (selfAccount is long sa && nameKey is long nk0 && records.FirstOrDefault(r => r.GetValueOrDefault(nk0) as string == selfName) is { } selfRec)
            accountKey = selfRec.Where(kv => kv.Value is long l && l == sa).Select(kv => (long?)kv.Key).FirstOrDefault();
        // Clan tag: short strings (empty without a clan) that differ between players; the realm ("EU") is the same for all.
        var clanKey = records.SelectMany(r => r).Where(kv => kv.Value is string s && s.Length <= 5 && !names.Contains(s))
            .GroupBy(kv => kv.Key)
            .Where(g => g.Count() == records.Count)
            .Select(g => (Key: g.Key, Distinct: g.Select(kv => (string)kv.Value!).Where(s => s.Length > 0).Distinct().Count()))
            .Where(x => x.Distinct >= 2)
            .OrderByDescending(x => x.Distinct).Select(x => (long?)x.Key).FirstOrDefault();

        var byName = new Dictionary<string, Dictionary<long, object?>>(StringComparer.Ordinal);
        if (nameKey is long nk)
            foreach (var r in records)
                if (r.GetValueOrDefault(nk) is string n) byName[n] = r;
        if (records.Count > 0 && vehicleKey is null) warnings.Add("Ships could not be matched to players; names are missing.");

        return arena.Select(a =>
        {
            var rec = byName.GetValueOrDefault(a.Name);
            var vehicle = rec is not null && vehicleKey is long vk && rec.GetValueOrDefault(vk) is long vid ? (int)vid : 0;
            var account = rec is not null && accountKey is long ak && rec.GetValueOrDefault(ak) is long acc ? acc : 0;
            var clan = rec is not null && clanKey is long ck && rec.GetValueOrDefault(ck) is string c && c.Length > 0 ? c : null;
            return new ReplayPlayer(a.Name, a.ShipId, a.Relation, vehicle, account, clan);
        }).ToList();
    }

    private sealed class ZoneBuilder(int id, float x, float z, float from)
    {
        public int Id { get; } = id;
        public float X { get; } = x;
        public float Z { get; } = z;
        public float From { get; } = from;
        public float? To { get; set; }
        public float Radius { get; set; }
        public string Name { get; set; } = "";
        public int Kind { get; set; } = -1;
        public int Index { get; set; } = -1;
        public List<(float T, int Team)> Owners { get; } = [];
    }

    private sealed class SmokeBuilder(int id, float from)
    {
        public int Id { get; } = id;
        public float From { get; } = from;
        public float? To { get; set; }
        public float Radius { get; set; }
        public List<(float X, float Z)> Points { get; set; } = [];
    }
}
