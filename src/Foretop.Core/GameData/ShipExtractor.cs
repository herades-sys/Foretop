using System.Globalization;
using static Foretop.Core.GameData.GameParamsReader;

namespace Foretop.Core.GameData;

/// <summary>
/// Turns GameParams + game texts into <see cref="ShipDataSet"/>: one record per ship with its stock and fully
/// upgraded configuration. Formulas for port values follow landaire/wows-toolkit (see docs/GAME_DATA.md).
/// </summary>
public static class ShipExtractor
{
    public const int FormatVersion = 4;

    public static ShipDataSet Extract(string build, Dictionary<object, object?> gp, GameTexts texts, IProgress<(int Done, int Total)>? progress = null,
        Action<string>? warn = null)
    {
        var upgrades = gp.Values.Where(e => TypeOf(e) == "Modernization").Select(e => ReadUpgrade(e, texts)).Where(u => u is not null)
            .ToDictionary(u => u!.Name, u => u!);
        var upgradeRules = gp.Values.Where(e => TypeOf(e) == "Modernization").ToList();

        var shipEntities = gp.Values.Where(e => TypeOf(e) == "Ship").ToList();
        var ships = new List<ShipData>(shipEntities.Count);
        for (var i = 0; i < shipEntities.Count; i++)
        {
            try
            {
                ships.Add(ReadShip(shipEntities[i], gp, texts, upgradeRules));
            }
            catch (Exception ex)
            {
                warn?.Invoke($"{Str(shipEntities[i], "name")}: {ex.Message}");
            }
            progress?.Report((i + 1, shipEntities.Count));
        }
        ships.Sort((a, b) => b.Tier != a.Tier ? b.Tier.CompareTo(a.Tier) : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        return new ShipDataSet { Build = build, ExtractedAt = DateTimeOffset.UtcNow, Ships = ships, Upgrades = upgrades, Maps = MapNames(texts) };
    }

    /// <summary>"IDS_SPACES/28_NAVAL_MISSION" = "Tears of the Desert" → key "28_naval_mission".</summary>
    public static Dictionary<string, string> MapNames(GameTexts texts)
    {
        const string prefix = "IDS_SPACES/";
        var maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in texts.WithPrefix(prefix))
        {
            var space = key[prefix.Length..];
            if (space.EndsWith("_DESCR", StringComparison.Ordinal) || space.Contains('/')) continue;
            maps[space.ToLowerInvariant()] = value;
        }
        return maps;
    }

    // ------------------------------------------------------------------ ship

    private static ShipData ReadShip(object? e, Dictionary<object, object?> gp, GameTexts texts, List<object?> upgradeRules)
    {
        var name = Str(e, "name")!;
        var index = Str(e, "index") ?? name.Split('_')[0];
        var typeinfo = Get(e, "typeinfo");
        var nation = Str(typeinfo, "nation") ?? "";
        var species = Str(typeinfo, "species") ?? "";
        var tier = (int)(Num(e, "level") ?? 0);
        var group = Str(e, "group") ?? "";

        var sui = Get(e, "ShipUpgradeInfo");
        var rawModules = Entries(sui).Where(kv => Get(kv.Value, "ucType") is string).Select(kv => (Name: (string)kv.Key, Node: kv.Value)).ToList();
        var stock = PickConfig(rawModules, top: false);
        var top = PickConfig(rawModules, top: true);

        var modules = rawModules.Select(m =>
        {
            var unit = gp.TryGetValue(m.Name, out var u) ? u : null;
            var prev = Str(m.Node, "prev");
            return new ModuleData(m.Name, texts.Ids(m.Name) ?? m.Name, Str(m.Node, "ucType")!, string.IsNullOrEmpty(prev) ? null : prev,
                Long(Num(unit, "costXP")), Long(Num(unit, "costCR")), stock.Contains(m.Name), top.Contains(m.Name));
        }).OrderBy(m => ModuleOrder(m.Type)).ThenBy(m => m.CostXP ?? 0).ToList();

        var stockConfig = BuildConfig(e, rawModules.Where(m => stock.Contains(m.Name)).ToList(), gp, texts, species);
        var topConfig = BuildConfig(e, rawModules.Where(m => top.Contains(m.Name)).ToList(), gp, texts, species);
        var topHull = ComponentOf(e, rawModules.Where(m => top.Contains(m.Name)).ToList(), "hull");
        var consumables = ReadConsumables(e, gp, texts);

        return new ShipData
        {
            Name = name,
            Index = index,
            Id = Long(Num(e, "id")) ?? 0,
            DisplayName = texts.Ids(index + "_FULL") ?? texts.Ids(index) ?? name,
            Description = texts.Ids(index + "_DESCR"),
            Year = texts.Ids(index + "_YEAR"),
            Nation = nation,
            NationName = texts.Ids(nation) ?? nation.Replace('_', ' '),
            Species = species,
            Tier = tier,
            Group = group,
            Availability = Availability(group),
            Obtainable = IsObtainable(group),
            IsPaperShip = Get(e, "isPaperShip") is true,
            Length = Items(Get(topHull, "size")).Select(v => v is double d ? d : v is long l ? l : (double?)null).FirstOrDefault(),
            Modules = modules,
            Stock = stockConfig,
            Top = topConfig,
            Consumables = consumables,
            UpgradeSlots = ApplicableUpgrades(name, group, nation, species, tier,
                consumables.SelectMany(c => c.Options).Select(o => o.Type).OfType<string>().ToHashSet(), upgradeRules),
            Armour = Plates(Get(topHull, "armor"), texts),
            HullModel = Str(topHull, "model"),
        };
    }

    /// <summary>Stock = every module type's root; top = the end of each research chain.</summary>
    private static HashSet<string> PickConfig(List<(string Name, object? Node)> modules, bool top)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var byType in modules.GroupBy(m => Str(m.Node, "ucType")))
        {
            var list = byType.ToList();
            if (!top)
            {
                chosen.Add((list.FirstOrDefault(m => string.IsNullOrEmpty(Str(m.Node, "prev"))) is { Name: not null } root ? root : list[0]).Name);
                continue;
            }
            int Depth(string n, int guard = 0)
            {
                var m = list.FirstOrDefault(x => x.Name == n);
                var prev = Str(m.Node, "prev");
                return string.IsNullOrEmpty(prev) || guard > 20 ? 0 : 1 + Depth(prev, guard + 1);
            }
            var leaves = list.Where(m => !list.Any(o => Str(o.Node, "prev") == m.Name)).ToList();
            chosen.Add((leaves.Count > 0 ? leaves : list).OrderByDescending(m => Depth(m.Name)).First().Name);
        }
        return chosen;
    }

    /// <summary>The component key for one component type: the hull lists what is allowed, other modules narrow it.</summary>
    private static string? ComponentKey(List<(string Name, object? Node)> config, string compType)
    {
        List<string>? set = null;
        foreach (var m in config.OrderBy(m => Str(m.Node, "ucType") == "_Hull" ? 0 : 1))
        {
            var list = Items(Get(Get(m.Node, "components"), compType)).OfType<string>().ToList();
            if (list.Count == 0) continue;
            if (set is null) set = list;
            else
            {
                var narrowed = set.Where(list.Contains).ToList();
                if (narrowed.Count > 0) set = narrowed;
            }
        }
        return set?.FirstOrDefault();
    }

    private static List<string> ComponentKeys(List<(string Name, object? Node)> config, string compType) =>
        config.SelectMany(m => Items(Get(Get(m.Node, "components"), compType)).OfType<string>()).Distinct().ToList();

    private static object? ComponentOf(object? ship, List<(string Name, object? Node)> config, string compType) =>
        ComponentKey(config, compType) is { } key ? Get(ship, key) : null;

    private static ShipConfig BuildConfig(object? ship, List<(string Name, object? Node)> config, Dictionary<object, object?> gp, GameTexts texts, string species)
    {
        var hull = ComponentOf(ship, config, "hull");
        var artillery = ComponentOf(ship, config, "artillery");
        var atba = ComponentOf(ship, config, "atba");
        var torpedoes = ComponentOf(ship, config, "torpedoes");
        var fireControl = ComponentOf(ship, config, "fireControl");
        var engine = ComponentOf(ship, config, "engine");
        var airDefense = ComponentKeys(config, "airDefense").Select(k => Get(ship, k)).Where(c => c is not null).ToList();
        var airArmament = ComponentKeys(config, "airArmament").Select(k => Get(ship, k)).Where(c => c is not null).ToList();

        var plates = Plates(Get(hull, "armor"), GameTexts.Empty).Where(p => p.Thickness > 0).ToList();
        var flood = Items(Get(hull, "floodNodes")).FirstOrDefault();
        var floodCoef = Items(flood).Select(ToDouble).FirstOrDefault();

        return new ShipConfig
        {
            HitPoints = Num(hull, "health"),
            MaxSpeed = Num(hull, "maxSpeed") is double s ? s * (1 + (Num(engine, "speedCoef") ?? 0)) : null,
            TurningRadius = Num(hull, "turningRadius"),
            RudderTime = Num(hull, "rudderTime") is double r ? r * 2.0 / 2.61 : null,
            DetectSea = Num(hull, "visibilityFactor"),
            DetectAir = Num(hull, "visibilityFactorByPlane"),
            DetectFiringInSmoke = Num(hull, "visibilityCoefGKInSmoke"),
            TorpedoProtection = floodCoef is double fc ? Math.Max(0, Math.Round((1 - fc / 0.333) * 100)) : null,
            EnginePower = Num(engine, "histEnginePower"),
            ArmourMin = plates.Count > 0 ? plates.Min(p => p.Thickness) : null,
            ArmourMax = plates.Count > 0 ? plates.Max(p => p.Thickness) : null,
            Main = ReadMain(artillery, fireControl, gp, texts),
            Secondary = ReadSecondaries(atba, texts),
            Torpedoes = ReadTorpedoes(torpedoes, gp, texts),
            AntiAir = ReadAntiAir([artillery, atba, .. airDefense]),
            HasAircraft = species == "AirCarrier",
            ModuleNames = config.Select(m => m.Name).ToList(),
        };
    }

    // ------------------------------------------------------------------ armament

    private static IEnumerable<object?> Mounts(object? component, string speciesName) =>
        Entries(component).Where(kv => kv.Key is string k && k.StartsWith("HP_", StringComparison.Ordinal) &&
                                       Str(Get(kv.Value, "typeinfo"), "species") == speciesName).Select(kv => kv.Value);

    private static MainBattery? ReadMain(object? artillery, object? fireControl, Dictionary<object, object?> gp, GameTexts texts)
    {
        var guns = Mounts(artillery, "Main").ToList();
        if (guns.Count == 0) return null;
        var first = guns[0];
        var range = (Num(artillery, "maxDist") ?? 0) * (Num(fireControl, "maxDistCoef") ?? 1) / 1000.0;
        double? dispersion = null;
        if (Num(first, "minRadius") is double minR && Num(first, "idealRadius") is double idealR && Num(first, "idealDistance") is double idealD && idealD > 0)
            dispersion = (minR + range / 30.0 * 1000 * (idealR - minR) / idealD) * 30;
        var rotation = Items(Get(first, "rotationSpeed")).Select(ToDouble).FirstOrDefault();
        var shells = guns.SelectMany(g => Items(Get(g, "ammoList")).OfType<string>()).Distinct()
            .Select(n => gp.TryGetValue(n, out var p) ? ReadShell(n, p, texts) : null).OfType<ShellData>()
            .OrderBy(s => s.Type switch { "AP" => 0, "CS" => 1, "HE" => 2, _ => 3 }).ToList();

        return new MainBattery
        {
            GunName = Str(first, "name") is { } gn ? texts.Ids(gn) ?? gn : "",
            Turrets = guns.GroupBy(g => (int)(Num(g, "numBarrels") ?? 1)).OrderByDescending(g => g.Count())
                .Select(g => new TurretGroup(g.Count(), g.Key)).ToList(),
            Guns = guns.Sum(g => (int)(Num(g, "numBarrels") ?? 1)),
            Calibre = Math.Round((Num(first, "barrelDiameter") ?? 0) * 1000),
            Reload = Num(first, "shotDelay") ?? 0,
            Turn180 = rotation is > 0 ? 180 / rotation : null,
            Range = range,
            Dispersion = dispersion,
            Sigma = Num(artillery, "sigmaCount") is double sc ? sc * (Num(fireControl, "sigmaCountCoef") ?? 1) : null,
            Shells = shells,
            TurretArmour = Plates(Get(first, "armor"), texts).Where(p => p.Thickness > 0).ToList(),
        };
    }

    private static ShellData? ReadShell(string name, object? p, GameTexts texts)
    {
        var type = Str(p, "ammoType");
        if (type is null) return null;
        return new ShellData
        {
            Name = name,
            DisplayName = texts.Ids(name) ?? name,
            Type = type,
            Damage = Num(p, "alphaDamage") ?? 0,
            Velocity = Num(p, "bulletSpeed") ?? 0,
            Mass = Num(p, "bulletMass") ?? 0,
            Diameter = Num(p, "bulletDiametr") ?? 0,
            Krupp = type == "AP" ? Num(p, "bulletKrupp") ?? 0 : 0,
            AirDrag = Num(p, "bulletAirDrag") ?? 0,
            FuseDelay = type == "AP" ? Num(p, "bulletDetonator") : null,
            FuseThreshold = type == "AP" ? Num(p, "bulletDetonatorThreshold") : null,
            RicochetAt = Num(p, "bulletRicochetAt") is double ra && ra <= 90 ? ra : null,
            AlwaysRicochetAt = Num(p, "bulletAlwaysRicochetAt") is double aa && aa <= 90 && Num(p, "bulletRicochetAt") <= 90 ? aa : null,
            Normalization = Num(p, "bulletCapNormalizeMaxAngle"),
            Capped = Get(p, "bulletCap") is true,
            FireChance = Num(p, "burnProb") is double b && b > 0 ? b : null,
            HePenetration = type == "HE" ? Num(p, "alphaPiercingHE") : null,
            SapPenetration = type == "CS" ? Num(p, "alphaPiercingCS") : null,
        };
    }

    private static Secondaries? ReadSecondaries(object? atba, GameTexts texts)
    {
        var guns = Mounts(atba, "Secondary").ToList();
        if (guns.Count == 0) return null;
        var groups = guns.GroupBy(g => (Calibre: Math.Round((Num(g, "barrelDiameter") ?? 0) * 1000), Barrels: (int)(Num(g, "numBarrels") ?? 1)))
            .OrderByDescending(g => g.Key.Calibre)
            .Select(g => new GunGroup(g.Count(), g.Key.Barrels, g.Key.Calibre, Str(g.First(), "name") is { } n ? texts.Ids(n) ?? n : ""))
            .ToList();
        var summary = string.Join(" · ", groups.Select(g => $"{g.Mounts} × {g.Barrels} {g.Calibre.ToString("0", CultureInfo.InvariantCulture)} mm"));
        return new Secondaries(summary, (Num(atba, "maxDist") ?? 0) / 1000.0, groups);
    }

    private static TorpedoArmament? ReadTorpedoes(object? component, Dictionary<object, object?> gp, GameTexts texts)
    {
        var launchers = Mounts(component, "Torpedo").ToList();
        if (launchers.Count == 0) return null;
        var first = launchers[0];
        var torpName = Items(Get(first, "ammoList")).OfType<string>().FirstOrDefault();
        var torp = torpName is not null && gp.TryGetValue(torpName, out var t) ? t : null;
        return new TorpedoArmament
        {
            Launchers = launchers.GroupBy(l => (int)(Num(l, "numBarrels") ?? 1)).Select(g => new TurretGroup(g.Count(), g.Key)).ToList(),
            Reload = Num(first, "shotDelay") ?? 0,
            TorpedoName = torpName is null ? "" : texts.Ids(torpName) ?? torpName,
            Damage = (Num(torp, "alphaDamage") ?? 0) / 3 + (Num(torp, "damage") ?? 0),
            Speed = Num(torp, "speed") ?? 0,
            Range = (Num(torp, "maxDist") ?? 0) * 30 / 1000.0,
            Detectability = Num(torp, "visibilityFactor"),
            FloodChance = Num(torp, "uwCritical") is double u ? Math.Round(u * 100) : null,
        };
    }

    private static AntiAir? ReadAntiAir(IEnumerable<object?> components)
    {
        double near = 0, medium = 0, far = 0, maxRange = 0, flakDamage = 0;
        var flak = 0;
        var any = false;
        foreach (var c in components)
        foreach (var (k, aura) in Entries(c))
        {
            if (k is not string key || !key.StartsWith("Aura", StringComparison.Ordinal)) continue;
            any = true;
            var period = Num(aura, "areaDamagePeriod") ?? 0;
            var dps = period > 0 ? (Num(aura, "areaDamage") ?? 0) / period : 0;
            switch (Str(aura, "type"))
            {
                case "near": near += dps; break;
                case "medium": medium += dps; break;
                default: far += dps; break;
            }
            if ((Num(aura, "innerBubbleCount") ?? 0) > 0)
            {
                flak += (int)((Num(aura, "innerBubbleCount") ?? 0) + (Num(aura, "outerBubbleCount") ?? 0));
                flakDamage = Math.Max(flakDamage, (Num(aura, "bubbleDamage") ?? 0) * 7);
            }
            maxRange = Math.Max(maxRange, (Num(aura, "maxDistance") ?? 0) / 1000.0);
        }
        return any ? new AntiAir(Math.Round(near), Math.Round(medium), Math.Round(far), maxRange, flak, flakDamage) : null;
    }

    // ------------------------------------------------------------------ consumables and upgrades

    private static List<ConsumableSlot> ReadConsumables(object? ship, Dictionary<object, object?> gp, GameTexts texts)
    {
        var slots = new List<ConsumableSlot>();
        foreach (var (_, slot) in Entries(Get(ship, "ShipAbilities")))
        {
            var options = new List<ConsumableOption>();
            foreach (var pair in Items(Get(slot, "abils")))
            {
                var parts = Items(pair).OfType<string>().ToList();
                if (parts.Count < 2) continue;
                var ability = gp.TryGetValue(parts[0], out var a) ? a : null;
                var variant = Get(ability, parts[1]);
                options.Add(new ConsumableOption(parts[0], parts[1],
                    texts.Get("IDS_DOCK_CONSUME_TITLE_" + parts[0].ToUpperInvariant()) ?? parts[0],
                    Str(variant, "consumableType"),
                    Num(variant, "numConsumables") is double n ? (int)n : null,
                    Num(variant, "reloadTime"), Num(variant, "workTime")));
            }
            if (options.Count > 0) slots.Add(new ConsumableSlot((int)(Num(slot, "slot") ?? slots.Count), options));
        }
        return slots.OrderBy(s => s.Slot).ToList();
    }

    private static UpgradeData? ReadUpgrade(object? m, GameTexts texts)
    {
        var name = Str(m, "name");
        if (name is null) return null;
        var effects = Entries(Get(m, "modifiers")).Where(kv => kv.Key is string k && k != "__class__")
            .Select(kv => ModifierText((string)kv.Key, kv.Value, texts)).Where(t => t is not null).Select(t => t!).ToList();
        var type = (int)(Num(m, "type") ?? 0);
        // Type 3 = unique upgrade (Legendary); the "ships" list is also used by generic upgrades, so it says nothing about uniqueness.
        return new UpgradeData(name, texts.Get("IDS_TITLE_" + name.ToUpperInvariant()) ?? name, (int)(Num(m, "slot") ?? -1), type,
            type == 3, effects, Long(Num(m, "costCR")));
    }

    /// <summary>Consumable-modification name token → the consumable type a ship must carry.</summary>
    private static readonly Dictionary<string, string> ConsumableForUpgrade = new(StringComparer.Ordinal)
    {
        ["CrashCrew"] = "crashCrew", ["SpeedBooster"] = "speedBoosters", ["AirDefenseDisp"] = "airDefenseDisp", ["Spotter"] = "scout",
        ["SonarSearch"] = "sonar", ["RegenCrew"] = "regenCrew", ["Smoke"] = "smokeGenerator", ["Fighter"] = "fighter", ["RLSSearch"] = "rls",
        ["TorpedoReloader"] = "torpedoReloader", ["ArtilleryBooster"] = "artilleryBoosters",
    };

    /// <summary>
    /// Which upgrades a ship can mount, per slot. Rules found in the data: slot -1 = retired; a ship named in
    /// <c>excludes</c> never, in <c>ships</c> always; otherwise the ship's group must be in <c>group</c> (empty = disabled),
    /// unique upgrades (type 3) only go to listed ships, an upgrade without tier/class/nation criteria only to listed
    /// ships, and consumable upgrades (type 1) need the matching consumable on board.
    /// </summary>
    private static List<UpgradeSlot> ApplicableUpgrades(string ship, string group, string nation, string species, int tier,
        HashSet<string> consumableTypes, List<object?> rules)
    {
        var bySlot = new SortedDictionary<int, List<string>>();
        foreach (var m in rules)
        {
            var slot = (int)(Num(m, "slot") ?? -1);
            var name = Str(m, "name");
            if (slot < 0 || name is null) continue;
            if (Items(Get(m, "excludes")).OfType<string>().Contains(ship)) continue;
            var listed = Items(Get(m, "ships")).OfType<string>().Contains(ship);
            if (!listed && !UpgradeFits(m, name, group, nation, species, tier, consumableTypes)) continue;
            if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = [];
            list.Add(name);
        }
        return bySlot.Select(kv => new UpgradeSlot(kv.Key, kv.Value)).ToList();
    }

    private static bool UpgradeFits(object? m, string name, string group, string nation, string species, int tier, HashSet<string> consumableTypes)
    {
        if (!Items(Get(m, "group")).OfType<string>().Contains(group)) return false;
        var type = (int)(Num(m, "type") ?? 0);
        if (type == 3) return false;
        var levels = Items(Get(m, "shiplevel")).Select(ToDouble).ToList();
        var types = Items(Get(m, "shiptype")).OfType<string>().ToList();
        var nations = Items(Get(m, "nation")).OfType<string>().ToList();
        if (levels.Count == 0 && types.Count == 0 && nations.Count == 0) return false;
        var fits = (levels.Count == 0 || levels.Contains(tier)) && (types.Count == 0 || types.Contains(species)) &&
                   (nations.Count == 0 || nations.Contains(nation));
        if (!fits) return false;
        if (type == 1)
        {
            var parts = name.Split('_');
            return parts.Length > 1 && ConsumableForUpgrade.TryGetValue(parts[1], out var need) && consumableTypes.Contains(need);
        }
        return true;
    }

    /// <summary>
    /// Readable text for a modifier. Coefficients become percentages; labels come from the curated table, then the game's
    /// own IDS_PARAMS_MODIFIER_* texts. A modifier with neither (engine-acceleration internals and the like) is left out
    /// rather than shown under its internal name, and so is one that changes nothing.
    /// </summary>
    internal static string? ModifierText(string key, object? value, GameTexts? texts = null)
    {
        var isCoeff = !Absolute.Contains(key) && (Coefficient.Contains(key) || key.EndsWith("Coeff", StringComparison.Ordinal) ||
            key.EndsWith("Coef", StringComparison.Ordinal) || key.EndsWith("Multiplier", StringComparison.Ordinal) ||
            key.EndsWith("Coefficient", StringComparison.Ordinal) || key.StartsWith("vulnerability", StringComparison.Ordinal));
        var unit = AbsoluteUnits.TryGetValue(key, out var u) ? u : "";
        string Fmt(double v) => isCoeff
            ? (v - 1 >= 0 ? "+" : "−") + Math.Abs((v - 1) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.##", CultureInfo.InvariantCulture) + unit;
        bool NoChange(double v) => isCoeff ? Math.Abs(v - 1) < 1e-9 : Math.Abs(v) < 1e-9;

        var upper = key.ToUpperInvariant();
        var label = ModifierLabels.TryGetValue(key, out var l) ? l
            : texts?.Get("IDS_PARAMS_MODIFIER_" + upper + "_SKILL") ?? texts?.Get("IDS_PARAMS_MODIFIER_" + upper);
        if (string.IsNullOrWhiteSpace(label)) return null;
        label = label.Replace(' ', ' ').Trim();
        switch (value)
        {
            case double or long:
                var d = ToDouble(value);
                if (d is null || NoChange(d.Value)) return null;
                return $"{label} {Fmt(d.Value)}";
            case PyObject or Dictionary<object, object?>:
                var parts = Entries(value).Where(kv => kv.Key is string k && k != "__class__" && ToDouble(kv.Value) is double dv && !NoChange(dv))
                    .GroupBy(kv => Fmt(ToDouble(kv.Value)!.Value))
                    .Select(g => g.Key + " (" + string.Join(", ", g.Select(kv => ClassShort((string)kv.Key))) + ")").ToList();
                return parts.Count == 0 ? null : $"{label} {string.Join(", ", parts)}";
            default:
                return null;
        }
    }

    private static string ClassShort(string species) => species switch
    {
        "Destroyer" => "DD", "Cruiser" => "CA", "Battleship" => "BB", "AirCarrier" => "CV", "Submarine" => "SS", _ => species,
    };

    private static readonly HashSet<string> Coefficient =
    [
        "GMShotDelay", "GMRotationSpeed", "GMMaxDist", "GMIdealRadius", "GSMaxDist", "GSIdealRadius", "GSShotDelay", "burnProb", "floodProb",
        "SGRudderTime", "speedCoef", "visibilityDistCoeff", "ConsumableReloadTime", "ConsumablesWorkTime", "torpedoSpeedMultiplier",
        "GTShotDelay", "GTRotationSpeed", "AABubbleDamage", "AAAuraDamage", "AAMaxHP", "GSMaxHP", "GMMaxHP", "GTMaxHP", "GMCritProb", "GTCritProb",
        "GMRepairTime", "GTRepairTime", "SGCritProb", "SGRepairTime", "engineCritProb", "engineRepairTime", "burnTime", "floodTime",
        "planeHealth", "planeSpeed", "planeVisibilityFactor", "airplanesForsageDuration", "squadronHealth", "prioritySectorCooldownMultiplier",
        "AAPrioritySectorCooldown", "smokeGeneratorLifeTime", "artilleryAlertMinDistance", "shootShift", "engineForwardUpTime", "engineBackwardUpTime",
    ];

    private static readonly HashSet<string> Absolute =
    [
        "AAInnerExtraBubbles", "AAExtraBubbles", "additionalConsumables", "visionXRayTorpedoDist", "dcNumPacksBonus", "planeExtraHangarSize",
        "smokeGeneratorAdditionalConsumables", "asAdditionalChargesCombined", "planeAdditionalConsumables",
    ];

    private static readonly Dictionary<string, string> AbsoluteUnits = new() { ["visionXRayTorpedoDist"] = " m" };

    private static readonly Dictionary<string, string> ModifierLabels = new()
    {
        ["GMShotDelay"] = "Main battery reload time", ["GMRotationSpeed"] = "Main battery traverse speed", ["GMMaxDist"] = "Main battery range",
        ["GMIdealRadius"] = "Main battery dispersion", ["GSMaxDist"] = "Secondary battery range", ["GSIdealRadius"] = "Secondary battery dispersion",
        ["GSShotDelay"] = "Secondary battery reload time", ["burnProb"] = "Risk of catching fire", ["floodProb"] = "Risk of flooding",
        ["SGRudderTime"] = "Rudder shift time", ["speedCoef"] = "Speed", ["visibilityDistCoeff"] = "Surface detectability",
        ["ConsumableReloadTime"] = "Consumable reload time", ["ConsumablesWorkTime"] = "Consumable action time",
        ["torpedoSpeedMultiplier"] = "Torpedo speed", ["GTShotDelay"] = "Torpedo tube reload time", ["GTRotationSpeed"] = "Torpedo tube traverse speed",
        ["AABubbleDamage"] = "Flak damage", ["AAAuraDamage"] = "Continuous AA damage", ["AAMaxHP"] = "AA mount HP", ["GSMaxHP"] = "Secondary battery HP",
        ["GMMaxHP"] = "Main battery survivability", ["GTMaxHP"] = "Torpedo tube survivability", ["GMCritProb"] = "Main battery risk of incapacitation",
        ["GTCritProb"] = "Torpedo tubes risk of incapacitation", ["GMRepairTime"] = "Main battery repair time", ["GTRepairTime"] = "Torpedo tube repair time",
        ["SGCritProb"] = "Steering gears risk of incapacitation", ["SGRepairTime"] = "Steering gears repair time",
        ["engineCritProb"] = "Engine risk of incapacitation", ["engineRepairTime"] = "Engine repair time", ["burnTime"] = "Fire duration",
        ["floodTime"] = "Flooding duration", ["planeHealth"] = "Aircraft HP", ["planeSpeed"] = "Aircraft speed",
        ["planeVisibilityFactor"] = "Aircraft detectability", ["prioritySectorCooldownMultiplier"] = "AA Defense Priority cooldown time",
        ["AAPrioritySectorCooldown"] = "AA Defense Priority cooldown time", ["AAInnerExtraBubbles"] = "Flak bursts",
        ["smokeGeneratorLifeTime"] = "Smoke screen duration",
        ["engineForwardUpTime"] = "Time to reach full power, forward", ["engineBackwardUpTime"] = "Time to reach full power, reverse",
        ["additionalConsumables"] = "Consumable charges", ["visibilityForSubmarineCoeff"] = "Detectability by submarines",
        ["GMAPDamageCoeff"] = "Main battery AP damage", ["torpedoDamageCoeff"] = "Torpedo damage", ["healthHullCoeff"] = "Hull HP",
        ["shootShift"] = "Dispersion of shells fired at your ship",
    };

    // ------------------------------------------------------------------ armour

    internal static List<ArmourPlate> Plates(object? armor, GameTexts texts)
    {
        var list = new List<ArmourPlate>();
        foreach (var (k, v) in Entries(armor))
        {
            var key = k switch { long l => (int)l, string s when int.TryParse(s, out var i) => i, _ => -1 };
            if (key < 0 || ToDouble(v) is not double mm) continue;
            var mat = key & 0xFFFF;
            var matName = ArmourMaterials.Name(mat);
            list.Add(new ArmourPlate(key, key >> 16, mat, matName, texts.Ids(matName) ?? matName.Replace('_', ' '), mm, Zone(matName)));
        }
        return list.OrderBy(p => p.Zone).ThenByDescending(p => p.Thickness).ToList();
    }

    /// <summary>Zone of a plate from its material name (rules from wows-toolkit zone_from_material_name).</summary>
    public static string Zone(string m)
    {
        static string FromToken(string t) => t switch
        {
            "Cit" or "OCit" => "Citadel", "Cas" => "Casemate", "SSC" or "SS" => "Superstructure", "Bow" => "Bow", "St" => "Stern", _ => "Hull",
        };
        if (m.StartsWith("Dual_", StringComparison.Ordinal)) return FromToken(m.Split('_')[1]);
        if (m.StartsWith("SG", StringComparison.Ordinal) || m.StartsWith("Rudder", StringComparison.Ordinal)) return "Steering gear";
        if (m.EndsWith("Cit", StringComparison.Ordinal)) return "Citadel";
        if (m.EndsWith("Cas", StringComparison.Ordinal)) return "Casemate";
        if (m.EndsWith("SSC", StringComparison.Ordinal) || m.EndsWith("SS", StringComparison.Ordinal)) return "Superstructure";
        if (m.EndsWith("Bow", StringComparison.Ordinal)) return "Bow";
        if (m.EndsWith("Stern", StringComparison.Ordinal)) return "Stern";
        if (m.StartsWith("Bow", StringComparison.Ordinal)) return "Bow";
        if (m.StartsWith("St_", StringComparison.Ordinal)) return "Stern";
        if (m.StartsWith("Cit", StringComparison.Ordinal) || m.StartsWith("OCit", StringComparison.Ordinal)) return "Citadel";
        if (m.StartsWith("Cas", StringComparison.Ordinal)) return "Casemate";
        if (m.StartsWith("SSC", StringComparison.Ordinal) || m.StartsWith("SS_", StringComparison.Ordinal)) return "Superstructure";
        if (m.StartsWith("Tur", StringComparison.Ordinal) || m.StartsWith("AuTurret", StringComparison.Ordinal) || m.StartsWith("Art", StringComparison.Ordinal))
            return "Turrets";
        if (m.StartsWith("Bulge", StringComparison.Ordinal)) return "Torpedo protection";
        if (m.StartsWith("Bridge", StringComparison.Ordinal) || m.StartsWith("Funnel", StringComparison.Ordinal)) return "Superstructure";
        return "Hull";
    }

    // ------------------------------------------------------------------ helpers

    private static int ModuleOrder(string ucType) => ucType switch
    {
        "_Hull" => 0, "_Artillery" => 1, "_Torpedoes" => 2, "_Suo" => 3, "_Engine" => 4, _ => 5,
    };

    internal static string Availability(string group) => group switch
    {
        "start" or "upgradeable" => "Tech tree",
        "premium" or "specialUnsellable" => "Premium",
        "special" or "ultimate" => "Special",
        "superShip" or "upgradeableUltimate" => "Supership",
        "upgradeableExclusive" => "Exclusive",
        "earlyAccess" => "Early access",
        "event" or "legendaryBattle" or "coopOnly" or "clan" => "Event",
        _ => "Not obtainable",
    };

    internal static bool IsObtainable(string group) =>
        Availability(group) is "Tech tree" or "Premium" or "Special" or "Supership" or "Exclusive" or "Early access";

    private static double? ToDouble(object? v) => v switch { long l => l, double d => d, _ => null };

    private static long? Long(double? v) => v is double d ? (long)d : null;
}
