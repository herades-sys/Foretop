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
}
