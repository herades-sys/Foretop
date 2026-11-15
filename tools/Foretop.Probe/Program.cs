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
        var defs = Foretop.Core.Replays.EntityDefs.Load(fs);
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
            var half = Foretop.Core.Replays.ReplayLibrary.PlayableHalf(System.Text.Encoding.UTF8.GetString(fs.Read(space + "/space.settings")));
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
        var defs = Foretop.Core.Replays.EntityDefs.Load(fs);
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
