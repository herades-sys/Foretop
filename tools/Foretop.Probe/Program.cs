using System.IO;
using System.Net.Http;
using System.Diagnostics;
using Foretop.Core.GameData;

// foretop-probe <game root> <command> [args]
//   vfs                 index statistics
//   ls <prefix> [n]     list files under a path
//   extract <path> <out>  write one decompressed file
var root = args.Length > 0 ? args[0] : @"F:\Wows";
var cmd = args.Length > 1 ? args[1] : "vfs";
var sw = Stopwatch.StartNew();
var fs = GameFileSystem.Open(root);
Console.WriteLine($"build {fs.Build}: {fs.Count:N0} files indexed in {sw.ElapsedMilliseconds} ms");

// The entity definitions of the build a replay was recorded with, when that build is still on disk (as Foretop does).
Foretop.Core.Replays.EntityDefs DefsFor(string replayPath)
{
    var b = Foretop.Core.Replays.ReplayFile.Read(replayPath).ClientBuild;
    if (b is null || b == fs.Build || !GameFileSystem.Builds(root).Contains(b)) return Foretop.Core.Replays.EntityDefs.Load(fs);
    Console.WriteLine($"  (definitions of build {b}, the replay's own)");
    return Foretop.Core.Replays.EntityDefs.Load(GameFileSystem.OpenBuild(root, b));
}

switch (cmd)
{
    case "vfs":
        foreach (var g in fs.Files.GroupBy(f => f.Path.Split('/')[0]).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"  {g.Key,-24} {g.Count(),8:N0} files  {g.Sum(f => (long)f.UnpackedSize) / 1048576.0,10:N1} MB");
        foreach (var p in new[] { "content/GameParams.data", "content/assets.bin" })
        {
            var f = fs.Find(p);
            Console.WriteLine(f is null ? $"  {p}: MISSING" : $"  {p}: {f.Package} @ {f.Offset}, {f.PackedSize:N0} -> {f.UnpackedSize:N0} bytes, compression 0x{f.Compression:X}");
        }
        break;
    case "ls":
        var n = args.Length > 3 ? int.Parse(args[3]) : 40;
        foreach (var f in fs.Under(args[2]).OrderBy(f => f.Path).Take(n)) Console.WriteLine($"  {f.Path}  ({f.UnpackedSize:N0} B)");
        break;
    case "gp":
    {
        // gp [entity name] [out.json]: load GameParams, print statistics, dump one entity
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        sw.Restart();
        var gp = GameParamsReader.Load(fs);
        var ms = sw.ElapsedMilliseconds;
        var after = GC.GetTotalMemory(true);
        Console.WriteLine($"GameParams: {gp.Count:N0} entities in {ms} ms, managed heap +{(after - before) / 1048576} MB, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
        foreach (var g in gp.Values.GroupBy(GameParamsReader.TypeOf).OrderByDescending(g => g.Count()).Take(25))
            Console.WriteLine($"  {g.Key ?? "(none)",-20} {g.Count(),7:N0}");
        if (args.Length > 2)
        {
            var hit = gp.Keys.OfType<string>().FirstOrDefault(k => k.Contains(args[2], StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"entity: {hit}");
            if (hit is not null)
            {
                var json = GameParamsReader.ToJson(gp[hit], maxDepth: 5, maxItems: 300);
                var outPath = args.Length > 3 ? args[3] : hit + ".json";
                File.WriteAllText(outPath, json);
                Console.WriteLine($"  wrote {json.Length:N0} chars to {outPath}");
            }
        }
        break;
    }
    case "dump":
    {
        // dump <outdir> <name-fragment>...: one JSON per matching entity (first match per fragment)
        var gp = GameParamsReader.Load(fs);
        Directory.CreateDirectory(args[2]);
        foreach (var frag in args.Skip(3))
        {
            var hit = gp.Keys.OfType<string>().FirstOrDefault(k => k.StartsWith(frag, StringComparison.OrdinalIgnoreCase))
                      ?? gp.Keys.OfType<string>().FirstOrDefault(k => k.Contains(frag, StringComparison.OrdinalIgnoreCase));
            if (hit is null) { Console.WriteLine($"  {frag}: no match"); continue; }
            File.WriteAllText(Path.Combine(args[2], hit + ".json"), GameParamsReader.ToJson(gp[hit], 6, 400));
            Console.WriteLine($"  {frag} -> {hit}");
        }
        break;
    }
    case "armour":
    {
        // armour <hull .model path>: read the hull armour geometry and print what it holds
        sw.Restart();
        var mesh = ArmourGeometry.LoadHull(fs, args[2]);
        if (mesh is null) { Console.WriteLine("  no armour geometry"); break; }
        Console.WriteLine($"  {mesh.Triangles.Count:N0} triangles from {string.Join(", ", mesh.Sources)} in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  bounds min {mesh.Min} max {mesh.Max}; length {mesh.Length:0.0} m, beam {mesh.Beam:0.0} m, height {mesh.Height:0.0} m");
        foreach (var g in mesh.Triangles.GroupBy(t => t.Key).OrderBy(g => g.Key))
        {
            var xs = g.SelectMany(t => new[] { t.A.X, t.B.X, t.C.X }).ToList();
            Console.WriteLine($"    {g.Key,8} layer {g.Key >> 16} {ArmourMaterials.Name(g.Key & 0xFFFF),-28} {g.Count(),5} tris  x {xs.Min(),7:0.0}..{xs.Max(),7:0.0}");
        }
        break;
    }
    case "field":
    {
        // field <Entity> <key>: the CLR type and value of one field (to check what the unpickler produced)
        var gp = GameParamsReader.Load(fs);
        var v = GameParamsReader.Get(gp[args[2]], args[3]);
        Console.WriteLine($"  {args[2]}.{args[3]} = {v} ({v?.GetType().FullName ?? "null"})");
        break;
    }
    case "type":
    {
        // type <TypeName> <out.json>: every entity of one typeinfo.type
        var gp = GameParamsReader.Load(fs);
        var all = gp.Where(kv => GameParamsReader.TypeOf(kv.Value) == args[2]).ToDictionary(kv => kv.Key, kv => kv.Value);
        File.WriteAllText(args[3], GameParamsReader.ToJson(all, 6, 5000));
        Console.WriteLine($"  {all.Count} entities of type {args[2]}");
        break;
    }
    case "ships":
    {
        // ships <out.json>: run the ship extractor over the whole client
        var gp = GameParamsReader.Load(fs);
        var texts = GameTexts.Load(root, fs.Build);
        sw.Restart();
        var warnings = new List<string>();
        var set = ShipExtractor.Extract(fs.Build, gp, texts, null, warnings.Add);
        Console.WriteLine($"extracted {set.Ships.Count} ships, {set.Upgrades.Count} upgrades in {sw.ElapsedMilliseconds} ms; {warnings.Count} warnings; texts {texts.Count:N0} ({texts.Language})");
        foreach (var w in warnings.Take(10)) Console.WriteLine("  ! " + w);
        Console.WriteLine($"  obtainable {set.Ships.Count(s => s.Obtainable)} of {set.Ships.Count}");
        foreach (var g in set.Ships.GroupBy(s => s.Availability).OrderByDescending(g => g.Count())) Console.WriteLine($"    {g.Key,-16} {g.Count(),5}");
        var json = System.Text.Json.JsonSerializer.Serialize(set, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(args[2], json);
        Console.WriteLine($"  wrote {json.Length / 1024:N0} KB");
        break;
    }
    case "extract":
        var bytes = fs.Read(args[2]);
        File.WriteAllBytes(args[3], bytes);
        Console.WriteLine($"  wrote {bytes.Length:N0} bytes to {args[3]}");
        break;
    case "groups":
    {
        // groups: ship count per GameParams "group", with examples
        var gp = GameParamsReader.Load(fs);
        var texts = GameTexts.Load(root, fs.Build);
        var ships = gp.Where(kv => GameParamsReader.TypeOf(kv.Value) == "Ship").Select(kv => (Name: kv.Key as string ?? "", Entity: kv.Value)).ToList();
        string? G(object? e) => e is Dictionary<object, object?> d && d.TryGetValue("group", out var g) ? g as string
            : e is PyObject o && o.Attrs.TryGetValue("group", out var og) ? og as string : null;
        foreach (var grp in ships.GroupBy(s => G(s.Entity) ?? "(none)").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {grp.Key,-22} {grp.Count(),5}  e.g. {string.Join(", ", grp.Take(6).Select(s => texts.Get("IDS_" + s.Name.Split('_')[0] + "_FULL") ?? s.Name))}");
        break;
    }
    case "texts":
    {
        // texts <substring>: game text keys containing the substring
        var texts = GameTexts.Load(root, fs.Build);
        foreach (var kv in texts.WithPrefix("IDS_").Where(kv => kv.Key.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(80))
            Console.WriteLine($"  {kv.Key} = {kv.Value}");
        break;
    }
    case "replay":
    {
        // replay <file.wowsreplay>: decode and summarise
        var defs = DefsFor(args[2]);
        sw.Restart();
        var file = Foretop.Core.Replays.ReplayFile.Read(args[2]);
        var data = Foretop.Core.Replays.ReplayParser.Parse(file, defs);
        Console.WriteLine($"decoded in {sw.ElapsedMilliseconds} ms: {data.MapSpace} {data.Scenario} {data.Duration:0} s, {file.Packets.Length:N0} B packets");
        foreach (var w in data.Warnings) Console.WriteLine("  warning: " + w);
        foreach (var pl in data.Players.OrderBy(x => x.Relation))
        {
            var tr = data.Tracks.GetValueOrDefault(pl.VehicleId);
            var seen = data.Seen.GetValueOrDefault(pl.VehicleId);
            var hp = data.Health.GetValueOrDefault(pl.VehicleId);
            Console.WriteLine($"  {pl.Relation} {pl.ClanTag,-5} {pl.Name,-24} veh {pl.VehicleId,8} acc {pl.AccountId,10} ship {pl.ShipId,11}: {tr?.Count ?? 0,5} pts, seen {seen?.Count ?? 0} spans {seen?.Sum(s => s.To - s.From) ?? 0:0} s, hp {data.MaxHealth.GetValueOrDefault(pl.VehicleId):0} → {hp?.LastOrDefault().Value:0}");
        }
        foreach (var d in data.Deaths) Console.WriteLine($"  death t {d.T:0}: {data.ByVehicle(d.Victim)?.Name ?? d.Victim.ToString()} by {data.ByVehicle(d.Killer)?.Name ?? d.Killer.ToString()} reason {d.Reason}");
        foreach (var z in data.Zones) Console.WriteLine($"  zone {z.Label} at {z.X:0},{z.Z:0} r {z.Radius:0.0} owners {string.Join(" ", z.Owners.Select(o => $"{o.Team}@{o.T:0}"))}");
        Console.WriteLine($"  smokes {data.Smokes.Count}, first: {(data.Smokes.Count > 0 ? $"{data.Smokes[0].From:0}-{data.Smokes[0].To:0} r {data.Smokes[0].Radius:0.0} pts {data.Smokes[0].Points.Count}" : "-")}");
        if (args.Length > 3)
        {
            // replay <file> <land-check>: share of track points that fall on land in minimap.png, for the scale
            var space = data.MapSpace.Trim('/');
            var png = fs.Read(space + "/minimap.png");
            var half = (float)(Foretop.Core.Replays.MapBounds.FromSpaceSettings(System.Text.Encoding.UTF8.GetString(fs.Read(space + "/space.settings"))).Width / 2);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "minimap.png"), png);
            using var pts = new StreamWriter(Path.Combine(Path.GetTempPath(), "tracks.csv"));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var tr in data.Tracks.Values) foreach (var q in tr)
                pts.WriteLine(((q.X + half) / (2 * half)).ToString("0.00000", inv) + ";" + ((half - q.Z) / (2 * half)).ToString("0.00000", inv));
            Console.WriteLine($"  half {half}; wrote tracks.csv and minimap.png to {Path.GetTempPath()}");
        }
        foreach (var c in data.Chat) Console.WriteLine($"  chat t {c.T:0} [{c.Channel}] {c.SenderId} {c.Sender?.Name ?? "?"}: {c.Text}");
        Console.WriteLine($"  clock {data.TimeLeft.Count} samples: {string.Join(" ", data.TimeLeft.Take(6).Select(v => $"{v.Value}@{v.T:0}"))} … {string.Join(" ", data.TimeLeft.TakeLast(3).Select(v => $"{v.Value}@{v.T:0}"))}");
        break;
    }
    case "battle-export":
    {
        // battle-export <foretop.db> <out.json> [anonymize]: newest recorded battle's roster with the statistics saved then
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={args[2]};Mode=ReadOnly");
        con.Open();
        using var q = con.CreateCommand();
        q.CommandText = "SELECT arena_json, summary_z FROM battles ORDER BY COALESCE(started_at, detected_at) DESC LIMIT 1";
        using var rd = q.ExecuteReader();
        if (!rd.Read()) { Console.WriteLine("no battle"); break; }
        var arenaJson = rd.GetString(0);
        var z = (byte[])rd[1];
        using var br = new System.IO.Compression.BrotliStream(new MemoryStream(z), System.IO.Compression.CompressionMode.Decompress);
        var summary = new StreamReader(br).ReadToEnd();
        var players = Foretop.Core.Stats.BattleSummaryCodec.Deserialize(summary);
        Foretop.Core.Game.ArenaInfoParser.TryParse(arenaJson, out var info, out _);
        var anon = args.Length > 4 && args[4] == "anonymize";
        var seq = 0;
        var names = new Dictionary<string, string>();
        string Name(string real, bool self) => !anon ? real : self ? "You" : names.TryGetValue(real, out var a) ? a : names[real] = $"Player {++seq}";
        var rows = info!.Vehicles.Select(v =>
        {
            var p = players.FirstOrDefault(x => string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase));
            var o = p?.Overall;
            return new
            {
                name = Name(v.Name, v.IsSelf), team = v.IsSelf ? "self" : v.IsFriendly ? "ally" : "enemy", shipId = v.ShipId,
                battles = o?.Battles, winRate = o?.WinRate is double w ? Math.Round(w, 1) : (double?)null,
                avgDamage = o?.AvgDamage is double d ? Math.Round(d) : (double?)null, pr = p?.Pr is double pr ? Math.Round(pr) : (double?)null,
                hidden = p?.Hidden,
            };
        }).ToList();
        File.WriteAllText(args[3], System.Text.Json.JsonSerializer.Serialize(new { map = info.MapName, type = info.MatchGroup, players = rows },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{rows.Count} players written");
        break;
    }
    case "spaces":
    {
        // spaces: chunk bounds of every map and the size of its minimap images
        static string Png(byte[]? b) => b is { Length: > 24 } ? $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16))}x{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20))}" : "-";
        foreach (var f in fs.Files.Where(f => f.Path.StartsWith("spaces/") && f.Path.EndsWith("/space.settings")).OrderBy(f => f.Path))
        {
            var dir = f.Path[..f.Path.LastIndexOf('/')];
            var x = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(fs.Read(f))).Root!;
            var b = x.Element("bounds");
            string V(string n) => b?.Element(n)?.Value.Trim() ?? "?";
            var land = fs.Find(dir + "/minimap.png") is { } lf ? fs.Read(lf) : null;
            var water = fs.Find(dir + "/minimap_water.png") is { } wf ? fs.Read(wf) : null;
            Console.WriteLine($"  {dir,-40} chunk {x.Element("chunkSize")?.Value.Trim(),-9} X {V("minX"),3}..{V("maxX"),-3} Y {V("minY"),3}..{V("maxY"),-3} bounds {Foretop.Core.Replays.MapBounds.FromSpaceSettings(x.ToString())}  land {Png(land),-9} water {Png(water)}");
        }
        break;
    }
    case "mapfit":
    {
        // mapfit <file.wowsreplay>: share of ship positions on island pixels of minimap.png for candidate transforms
        var defs = DefsFor(args[2]);
        var data = Foretop.Core.Replays.ReplayParser.Parse(Foretop.Core.Replays.ReplayFile.Read(args[2]), defs);
        var space = data.MapSpace.Trim('/');
        if (!space.StartsWith("spaces/")) space = "spaces/" + space;
        var bnd = Foretop.Core.Replays.MapBounds.FromSpaceSettings(System.Text.Encoding.UTF8.GetString(fs.Read(fs.Find(space + "/space.settings")!)));
        var half = bnd.Width / 2;
        var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(fs.Read(fs.Find(space + "/minimap.png")!)),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(dec.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        bool Land(double u, double v)
        {
            var ix = (int)(u * w); var iy = (int)(v * h);
            return ix >= 0 && iy >= 0 && ix < w && iy < h && px[(iy * w + ix) * 4 + 3] > 128;
        }
        var points = data.Tracks.Values.SelectMany(t => t).Select(p => (p.X, p.Z)).ToList();
        var landShare = Enumerable.Range(0, w * h).Count(i => px[i * 4 + 3] > 128) / (double)(w * h);
        Console.WriteLine($"{space}: image {w}x{h}, island pixels {landShare:P1}, {points.Count:N0} track points, |x| max {points.Max(p => Math.Abs(p.X)):0}, |z| max {points.Max(p => Math.Abs(p.Z)):0}, playable half {half}");
        double Hits(double hh, double dx, double dz, bool flipX, bool flipZ) =>
            points.Count(p => Land(((flipX ? -p.X : p.X) - dx + hh) / (2 * hh), (hh - (flipZ ? -p.Z : p.Z) + dz) / (2 * hh))) / (double)points.Count;
        Console.WriteLine($"  current transform (half {half}, north up): {Hits(half, 0, 0, false, false):P2} of positions on islands");
        foreach (var (fx, fz, name) in new[] { (true, false, "X mirrored"), (false, true, "Z mirrored"), (true, true, "rotated 180°") })
            Console.WriteLine($"  {name,-14}: {Hits(half, 0, 0, fx, fz):P2}");
        var best = (from hh in Enumerable.Range(50, 70).Select(i => i * 10.0)
                    from dx in new[] { -50.0, -25, 0, 25, 50 }
                    from dz in new[] { -50.0, -25, 0, 25, 50 }
                    select (hh, dx, dz, hits: Hits(hh, dx, dz, false, false))).OrderBy(r => r.hits).Take(8).ToList();
        foreach (var r in best) Console.WriteLine($"  half {r.hh,4} offset {r.dx,4},{r.dz,4}: {r.hits:P2}");
        foreach (var hh in new[] { half - 100, half - 50, half, half + 50, half + 100, half + 200 }) Console.WriteLine($"  half {hh,4}: {Hits(hh, 0, 0, false, false):P2}");
        // Heading: yaw against the direction of travel (atan2(dx, dz): 0 = north, clockwise positive).
        var diffs = new List<double>();
        foreach (var tr in data.Tracks.Values)
            for (var i = 1; i < tr.Count; i++)
            {
                var (a, b2) = (tr[i - 1], tr[i]);
                if (b2.T - a.T is <= 0 or > 3) continue;
                double ddx = b2.X - a.X, ddz = b2.Z - a.Z;
                if (Math.Sqrt(ddx * ddx + ddz * ddz) < 1.5) continue;
                var d = Math.Atan2(ddx, ddz) - b2.Yaw;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                diffs.Add(Math.Abs(d) * 180 / Math.PI);
            }
        diffs.Sort();
        if (diffs.Count > 0)
            Console.WriteLine($"  heading vs travel ({diffs.Count:N0} moving samples): median {diffs[diffs.Count / 2]:0.0}°, 90th percentile {diffs[(int)(diffs.Count * 0.9)]:0.0}°");
        if (args.Contains("--per-ship"))
            foreach (var (vid, tr) in data.Tracks.OrderBy(kv => kv.Key))
            {
                // Per ship: share of positions on islands, and of moving samples within 30° of straight backwards (reversing).
                var onLand = tr.Count(p => Land((p.X + half) / (2 * half), (half - p.Z) / (2 * half)));
                int moving = 0, back = 0, side = 0;
                for (var i = 1; i < tr.Count; i++)
                {
                    var (a, b2) = (tr[i - 1], tr[i]);
                    if (b2.T - a.T is <= 0 or > 3) continue;
                    double ddx = b2.X - a.X, ddz = b2.Z - a.Z;
                    if (Math.Sqrt(ddx * ddx + ddz * ddz) < 1.5) continue;
                    var d = Math.Atan2(ddx, ddz) - b2.Yaw;
                    while (d > Math.PI) d -= 2 * Math.PI;
                    while (d < -Math.PI) d += 2 * Math.PI;
                    var deg = Math.Abs(d) * 180 / Math.PI;
                    moving++;
                    if (deg > 150) back++; else if (deg > 30) side++;
                }
                var pl = data.ByVehicle(vid);
                Console.WriteLine($"    {vid,9} {pl?.Name ?? "?",-24} ship {pl?.ShipId,-11} pts {tr.Count,5} island {(tr.Count > 0 ? onLand / (double)tr.Count : 0),7:P2}  moving {moving,5} reversing {(moving > 0 ? back / (double)moving : 0),6:P1} sideways {(moving > 0 ? side / (double)moving : 0),6:P1}");
            }
        break;
    }
    case "api":
    {
        // api <method> <query>: one Wargaming API GET (EU) with the application ID from Foretop's settings; the ID is
        // decrypted in memory only and never printed. Example: api ships/stats "account_id=1&fields=pvp.battles"
        var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foretop", "settings.json")));
        var prot = settings.RootElement.GetProperty("ProtectedApplicationId").GetString()!;
        var appId = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(prot),
            System.Text.Encoding.UTF8.GetBytes("Broadside.ApplicationId.v1"), System.Security.Cryptography.DataProtectionScope.CurrentUser));
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"https://api.worldofwarships.eu/wows/{args[2].Trim('/')}/?application_id={appId}&{args[3]}");
        Console.WriteLine(body.Replace(appId, "***"));
        break;
    }
    case "unpack":
    {
        // unpack <hex file> <out.json>: Foretop's compressed cache value (hex of api_cache.zjson) as JSON
        var hex = File.ReadAllText(args[2]).Trim();
        using var input = new MemoryStream(Convert.FromHexString(hex));
        using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, System.Text.Encoding.UTF8);
        File.WriteAllText(args[3], reader.ReadToEnd());
        break;
    }
    case "facts":
    {
        // facts <file.wowsreplay> [apiDamage apiXp]: the personal battle facts Foretop reads from a replay
        var defs = DefsFor(args[2]);
        var data = Foretop.Core.Replays.ReplayParser.Parse(Foretop.Core.Replays.ReplayFile.Read(args[2]), defs);
        var self = data.Self!;
        Foretop.Core.Stats.StatTotals? api = args.Length > 4
            ? new Foretop.Core.Stats.StatTotals(1, 0, 0, 0, long.Parse(args[3]), long.Parse(args[4]), 0, 0, 0) : null;
        var o = Foretop.Core.Personal.ReplayFacts.Compute(data.ResultsJson, data.SelfAccountId ?? 0, self.ShipId, self.VehicleId, 0,
            data.Players, data.Deaths, data.TimeLeft, data.Duration, api);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(o.Facts));
        Console.WriteLine("note: " + (o.Note ?? "-"));
        break;
    }
    case "results":
    {
        // results <file.wowsreplay> <out.json>: the battle-results packet (type 0x22) as written in the replay
        var file = Foretop.Core.Replays.ReplayFile.Read(args[2]);
        var pk = file.Packets;
        for (var p = 0; p + 12 <= pk.Length;)
        {
            var size = BitConverter.ToInt32(pk, p);
            var type = BitConverter.ToUInt32(pk, p + 4);
            var t = BitConverter.ToSingle(pk, p + 8);
            if (type == 0x22)
            {
                var body = pk.AsSpan(p + 12, size);
                var start = body.IndexOf((byte)'{');
                Console.WriteLine($"0x22 at t {t:0.0}: {size:N0} bytes, JSON starts at {start}, first bytes {Convert.ToHexString(body[..Math.Min(8, size)])}");
                File.WriteAllBytes(args[3], body[start..].ToArray());
            }
            p += 12 + size;
        }
        break;
    }
    case "defs":
    {
        // defs <Entity> [n]: client property and method indices as replays use them
        var defs = Foretop.Core.Replays.EntityDefs.Load(fs);
        Console.WriteLine("types: " + string.Join(", ", defs.TypeNames.Select((t, i) => $"{i + 1}={t}")));
        var e = defs.Entity(args[2])!;
        var max = args.Length > 3 ? int.Parse(args[3]) : 400;
        for (var i = 0; i < Math.Min(max, e.Properties.Count); i++)
            Console.WriteLine($"  prop {i,3} {e.Properties[i].Name,-36} {e.Properties[i].Size,6} {e.Properties[i].Flags}");
        for (var i = 0; i < Math.Min(max, e.ClientMethods.Count); i++)
        {
            Console.WriteLine($"  meth {i,3} {e.ClientMethods[i].Name,-36} {e.ClientMethods[i].Size,6}");
            // defs <Entity> <n> <filter>: also the arguments of the matching methods
            if (args.Length > 4 && e.ClientMethods[i].Name.Contains(args[4], StringComparison.OrdinalIgnoreCase))
                foreach (var a in e.ClientMethods[i].Args) Console.WriteLine($"        {a.Name,-24} {a.Type.ToString().Replace("\n", " ")}");
        }
        break;
    }
}
