using System.Globalization;
using Foretop.Core.GameData;

namespace Foretop.App.ViewModels;

/// <summary>A labelled value. Tag marks where a number comes from when it is not read directly ("calculated", "theoretical").</summary>
public sealed record FactRow(string Label, string Value, string? Tag = null);

public sealed record FactSection(string Title, IReadOnlyList<FactRow> Rows, string? Note = null);

public sealed record PenetrationRow(string Range, string Belt, string Time, string Angle);

public sealed record ShellCard(string Kind, string Name, string Headline, IReadOnlyList<FactRow> Rows, IReadOnlyList<PenetrationRow> Penetration);

/// <summary>Formats ship data for display. Missing values come out as "—" text, never as 0.</summary>
public static class ShipFacts
{
    private static CultureInfo C => CultureInfo.CurrentCulture;
    public const string Missing = "—";

    public static string N0(double? v) => v is double d ? d.ToString("N0", C) : Missing;
    public static string N(double? v, string format) => v is double d ? d.ToString(format, C) : Missing;
    public static string Km(double? v) => v is double d ? d.ToString("0.##", C) + " km" : Missing;
    public static string Sec(double? v) => v is double d ? d.ToString("0.#", C) + " s" : Missing;
    public static string Mm(double? v) => v is double d ? d.ToString("0.#", C) + " mm" : Missing;
    public static string Pct(double? v) => v is double d ? d.ToString("0.#", C) + "%" : Missing;
    public static string Kn(double? v) => v is double d ? d.ToString("0.#", C) + " kn" : Missing;

    public static string ClassName(string species) => species switch
    {
        "Destroyer" => "Destroyer", "Cruiser" => "Cruiser", "Battleship" => "Battleship", "AirCarrier" => "Aircraft carrier",
        "Submarine" => "Submarine", "Auxiliary" => "Auxiliary", _ => species,
    };

    public static string TierRoman(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X", 11 => "★",
        _ => tier.ToString(C),
    };

    public static string MainSummary(ShipConfig c) => c.Main is { } m ? $"{m.Guns} × {m.Calibre.ToString("0", C)} mm" : Missing;

    public static string ArmourRange(ShipConfig c) =>
        c.ArmourMin is double a && c.ArmourMax is double b ? $"{a.ToString("0", C)}–{b.ToString("0", C)} mm" : Missing;

    public static string Turrets(MainBattery m) => string.Join(" + ", m.Turrets.Select(t => $"{t.Count} × {t.Barrels}"));

    public static IReadOnlyList<FactRow> KeyFigures(ShipConfig c) =>
    [
        new("Hit points", N0(c.HitPoints)),
        new("Main battery", MainSummary(c)),
        new("Firing range", Km(c.Main?.Range)),
        new("Maximum speed", Kn(c.MaxSpeed)),
        new("Surface detectability", Km(c.DetectSea)),
        new("Armour", ArmourRange(c)),
    ];

    public static FactSection General(ShipConfig c) => new("General characteristics",
    [
        new("Hit points", N0(c.HitPoints)),
        new("Armour", ArmourRange(c)),
        new("Maximum speed", Kn(c.MaxSpeed)),
        new("Turning circle radius", c.TurningRadius is double t ? t.ToString("N0", C) + " m" : Missing),
        new("Rudder shift time", Sec(c.RudderTime)),
        new("Surface detectability", Km(c.DetectSea)),
        new("Air detectability", Km(c.DetectAir)),
        new("Main battery range", Km(c.Main?.Range)),
        new("Secondary battery range", c.Secondary is { } s ? Km(s.Range) : "No secondary battery"),
        new("Torpedo range", c.Torpedoes is { } tp ? Km(tp.Range) : "No torpedoes"),
        new("AA range", c.AntiAir is { } aa ? Km(aa.MaxRange) : "No AA guns"),
    ]);

    public static FactSection? MainArmament(ShipConfig c)
    {
        if (c.Main is not { } m) return null;
        return new FactSection("Main armament",
        [
            new("Turrets", Turrets(m)),
            new("Guns", m.Guns.ToString(C)),
            new("Calibre", Mm(m.Calibre)),
            new("Reload time", Sec(m.Reload)),
            new("180° turn time", Sec(m.Turn180)),
            new("Maximum range", Km(m.Range)),
            new("Dispersion at maximum range", m.Dispersion is double d ? d.ToString("0", C) + " m" : Missing),
            new("Sigma", N(m.Sigma, "0.0#")),
        ], $"{Turrets(m)} {m.GunName}");
    }

    public static IReadOnlyList<ShellCard> Shells(ShipConfig c)
    {
        if (c.Main is not { } m) return [];
        var cards = new List<ShellCard>();
        var salvo = m.Guns;
        foreach (var s in m.Shells)
        {
            var dpm = m.Reload > 0 ? s.Damage * salvo * 60 / m.Reload : (double?)null;
            var rows = new List<FactRow>
            {
                new("Maximum damage", N0(s.Damage)),
                new("Muzzle velocity", s.Velocity.ToString("N0", C) + " m/s"),
            };
            if (s.Type == "HE") rows.Add(new("Chance to start a fire", s.FireChance is double f ? (f * 100).ToString("0.#", C) + "%" : Missing));
            if (s.Type == "HE") rows.Add(new("HE penetration", Mm(s.HePenetration)));
            if (s.Type == "CS") rows.Add(new("SAP penetration", Mm(s.SapPenetration)));
            rows.Add(new("Overmatches plates under", Mm(Math.Round(Ballistics.OvermatchThreshold(m.Calibre), 1)), "calculated"));
            rows.Add(new("Damage per minute", dpm is double dd ? dd.ToString("N0", C) : Missing, "theoretical"));
            if (s.Type != "HE")
            {
                rows.Add(new("Shell mass", s.Mass.ToString("N0", C) + " kg"));
                rows.Add(new("Ricochet angles", s.RicochetAt is double r && s.AlwaysRicochetAt is double ar ? $"{r:0}°–{ar:0}°" : Missing));
            }
            if (s.Type == "AP")
            {
                rows.Add(new("Fuse delay", N(s.FuseDelay, "0.0##") + " s"));
                rows.Add(new("Fuse arming threshold", Mm(s.FuseThreshold)));
            }
            var pen = s.Type == "AP" && s.Krupp > 0
                ? Ballistics.Table(s, m.Range).Select(p => new PenetrationRow(Km(Math.Round(p.RangeKm, 1)), Mm(Math.Round(p.BeltPenetration)),
                    Sec(Math.Round(p.FlightTime, 1)), p.FallAngle.ToString("0.0", C) + "°")).ToList()
                : [];
            cards.Add(new ShellCard(s.Type == "CS" ? "SAP" : s.Type, s.DisplayName, N0(s.Damage) + " dmg", rows, pen));
        }
        return cards;
    }

    public static IReadOnlyList<FactRow> Summary(ShipData ship, ShipConfig c, Dictionary<string, UpgradeData> upgrades)
    {
        var rows = new List<FactRow>
        {
            new("Secondary armament", c.Secondary is { } s ? $"{s.Summary} · {Km(s.Range)}" : "No secondary battery"),
            new("Torpedoes", c.Torpedoes is { } t
                ? $"{string.Join(" + ", t.Launchers.Select(l => $"{l.Count} × {l.Barrels}"))} · {t.TorpedoName} · {N0(t.Damage)} dmg · {Kn(t.Speed)} · {Km(t.Range)}"
                : "This ship carries no torpedoes"),
            new("Anti-aircraft", c.AntiAir is { } aa
                ? string.Join(" · ", new[]
                {
                    aa.ShortDps > 0 ? $"short range {aa.ShortDps.ToString("N0", C)} per second" : null,
                    aa.MediumDps > 0 ? $"medium range {aa.MediumDps.ToString("N0", C)} per second" : null,
                    aa.LongDps > 0 ? $"long range {aa.LongDps.ToString("N0", C)} per second" : null,
                    aa.FlakClouds > 0 ? $"flak {aa.FlakClouds} × {aa.FlakDamage.ToString("N0", C)}" : null,
                }.Where(x => x is not null))
                : "No AA guns"),
            new("Aircraft", c.HasAircraft ? "Carrier air groups" : "No attack squadrons"),
            new("Consumables", ship.Consumables.Count == 0 ? "None" : string.Join(" · ", ship.Consumables.Select(sl => string.Join(" or ", sl.Options.Select(o => o.DisplayName))))),
            new("Concealment", $"{Km(c.DetectSea)} by sea · {Km(c.DetectAir)} by air" + (c.DetectFiringInSmoke is double sm ? $" · {Km(sm)} firing in smoke" : "")),
            new("Mobility", $"{Kn(c.MaxSpeed)} · {N0(c.TurningRadius)} m turning radius · {Sec(c.RudderTime)} rudder" +
                            (c.EnginePower is double hp ? $" · {hp.ToString("N0", C)} hp" : "")),
            new("Upgrades", $"{ship.UpgradeSlots.Count} slots · {ship.UpgradeSlots.Sum(u => u.Options.Count)} options"),
        };
        return rows;
    }
}
