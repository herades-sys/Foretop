namespace Foretop.Core.GameData;

// Compact ship data extracted from the game client once per build (see ShipExtractor). Plain records so it
// serialises to JSON. Every value is read from the game files unless the property says "calculated".
// A missing value is null, never 0 or an estimate.

public sealed record ShipDataSet
{
    public required string Build { get; init; }
    public required DateTimeOffset ExtractedAt { get; init; }
    public required List<ShipData> Ships { get; init; }
    public required Dictionary<string, UpgradeData> Upgrades { get; init; }
    /// <summary>Map names from the game texts, keyed by the space name in lower case (e.g. "28_naval_mission").</summary>
    public Dictionary<string, string> Maps { get; init; } = [];
    public int FormatVersion { get; init; } = ShipExtractor.FormatVersion;
}

public sealed record ShipData
{
    public required string Name { get; init; }          // GameParams key, e.g. PJSB018_Yamato_1944
    public required string Index { get; init; }         // short code, e.g. PJSB018
    public long Id { get; init; }                       // same number as the Wargaming API ship_id
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public required string Nation { get; init; }        // game key, e.g. Japan
    public required string NationName { get; init; }
    public required string Species { get; init; }       // Destroyer, Cruiser, Battleship, AirCarrier, Submarine
    public int Tier { get; init; }
    public required string Group { get; init; }         // raw GameParams group
    public required string Availability { get; init; }  // Tech tree, Premium, Special, Supership, …
    public bool Obtainable { get; init; }

    /// <summary>
    /// A ship the game marks as in testing (group demoWithoutStats / demoWithoutStatsPrem). Only its identity is
    /// extracted: no characteristics, damage, armour, modules or equipment, and no player statistics are shown for it.
    /// </summary>
    public bool IsTestShip { get; init; }
    public bool IsPaperShip { get; init; }
    public double? Length { get; init; }                // metres (hull size[0])
    public required List<ModuleData> Modules { get; init; }
    public required ShipConfig Stock { get; init; }
    public required ShipConfig Top { get; init; }
    public required List<ConsumableSlot> Consumables { get; init; }
    public required List<UpgradeSlot> UpgradeSlots { get; init; }
    public required List<ArmourPlate> Armour { get; init; }
    public string? HullModel { get; init; }             // .model path of the top hull, for the armour geometry
}

public sealed record ModuleData(string Name, string DisplayName, string Type, string? Prev, long? CostXP, long? CostCR, bool InStock, bool InTop);

/// <summary>One module configuration (stock or fully upgraded).</summary>
public sealed record ShipConfig
{
    public double? HitPoints { get; init; }
    public double? MaxSpeed { get; init; }              // knots
    public double? TurningRadius { get; init; }         // m
    public double? RudderTime { get; init; }            // s, as shown in port
    public double? DetectSea { get; init; }             // km
    public double? DetectAir { get; init; }
    public double? DetectFiringInSmoke { get; init; }
    public double? TorpedoProtection { get; init; }     // % damage reduction
    public double? EnginePower { get; init; }           // hp
    public double? ArmourMin { get; init; }             // mm, hull plates
    public double? ArmourMax { get; init; }
    public MainBattery? Main { get; init; }
    public Secondaries? Secondary { get; init; }
    public TorpedoArmament? Torpedoes { get; init; }
    public AntiAir? AntiAir { get; init; }
    public bool HasAircraft { get; init; }
    public List<string> ModuleNames { get; init; } = [];
}

public sealed record MainBattery
{
    public required string GunName { get; init; }
    public required List<TurretGroup> Turrets { get; init; } // e.g. 3 turrets × 3 barrels
    public int Guns { get; init; }
    public double Calibre { get; init; }                // mm
    public double Reload { get; init; }                 // s
    public double? Turn180 { get; init; }               // s
    public double Range { get; init; }                  // km
    public double? Dispersion { get; init; }            // m at max range
    public double? Sigma { get; init; }
    public required List<ShellData> Shells { get; init; }
    public required List<ArmourPlate> TurretArmour { get; init; }
}

public sealed record TurretGroup(int Count, int Barrels);

public sealed record ShellData
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Type { get; init; }          // AP, HE, CS (SAP)
    public double Damage { get; init; }
    public double Velocity { get; init; }               // m/s
    public double Mass { get; init; }                   // kg
    public double Diameter { get; init; }               // m
    public double Krupp { get; init; }
    public double AirDrag { get; init; }
    public double? FuseDelay { get; init; }             // s
    public double? FuseThreshold { get; init; }         // mm
    public double? RicochetAt { get; init; }            // degrees
    public double? AlwaysRicochetAt { get; init; }
    public double? Normalization { get; init; }
    public bool Capped { get; init; }
    public double? FireChance { get; init; }            // 0..1
    public double? HePenetration { get; init; }         // mm
    public double? SapPenetration { get; init; }        // mm
}

public sealed record Secondaries(string Summary, double Range, List<GunGroup> Groups);

public sealed record GunGroup(int Mounts, int Barrels, double Calibre, string GunName);

public sealed record TorpedoArmament
{
    public required List<TurretGroup> Launchers { get; init; }
    public double Reload { get; init; }
    public required string TorpedoName { get; init; }
    public double Damage { get; init; }
    public double Speed { get; init; }                  // knots
    public double Range { get; init; }                  // km
    public double? Detectability { get; init; }         // km
    public double? FloodChance { get; init; }           // %
}

public sealed record AntiAir(double ShortDps, double MediumDps, double LongDps, double MaxRange, int FlakClouds, double FlakDamage);

public sealed record ConsumableSlot(int Slot, List<ConsumableOption> Options);

public sealed record ConsumableOption(string Name, string Variant, string DisplayName, string? Type, int? Charges, double? Reload, double? WorkTime);

public sealed record UpgradeSlot(int Slot, List<string> Options);

public sealed record UpgradeData(string Name, string DisplayName, int Slot, int Type, bool ShipSpecific, List<string> Effects, long? CostCR);

/// <summary>One armour plate: GameParams key = (layer &lt;&lt; 16) | material.</summary>
public sealed record ArmourPlate(int Key, int Layer, int Material, string MaterialName, string Title, double Thickness, string Zone);
