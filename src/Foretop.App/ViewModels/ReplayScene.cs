using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foretop.Core.Replays;

namespace Foretop.App.ViewModels;

/// <summary>One ship of a decoded replay, with lookups by time for the minimap and the team lists.</summary>
public sealed class SceneShip
{
    public required ReplayPlayer Player { get; init; }
    public required string ShipName { get; init; }
    public required string Species { get; init; }   // Destroyer, Cruiser, Battleship, AirCarrier, Submarine, ""
    public required TrackPoint[] Track { get; init; }
    public required Visibility[] Seen { get; init; }
    public required ValuePoint[] Health { get; init; }
    public float MaxHealth { get; init; }
    public float? DeathAt { get; init; }
    public string? KilledBy { get; init; }

    /// <summary>A ship in testing: its position is shown, its health (hit points) is not.</summary>
    public bool IsTestShip { get; init; }

    /// <summary>A gap between track points longer than this (seconds) means the ship was out of view: not interpolated.</summary>
    public const float MaxGap = 6;

    public int Relation => Player.Relation;
    public bool IsSelf => Player.IsSelf;

    public bool IsDead(float t) => DeathAt is float d && d <= t;

    /// <summary>True while the recording client had the ship in view (its position is current).</summary>
    public bool VisibleAt(float t)
    {
        foreach (var s in Seen) if (s.From <= t && t <= s.To) return true;
        return false;
    }

    /// <summary>End of the last visible span that started before <paramref name="t"/>; null when never seen yet.</summary>
    public float? LastSeenBefore(float t)
    {
        float? last = null;
        foreach (var s in Seen) if (s.From <= t) last = Math.Min(s.To, t);
        return last;
    }

    /// <summary>Interpolated position at <paramref name="t"/> (clamped to the first and last known point).</summary>
    public bool TryPosition(float t, out float x, out float z, out float yaw)
    {
        x = z = yaw = 0;
        var tr = Track;
        if (tr.Length == 0 || t < tr[0].T - 0.5f) return false;
        var i = Lower(tr, t);
        if (i <= 0) { (x, z, yaw) = (tr[0].X, tr[0].Z, tr[0].Yaw); return true; }
        if (i >= tr.Length) { var l = tr[^1]; (x, z, yaw) = (l.X, l.Z, l.Yaw); return true; }
        var a = tr[i - 1];
        var b = tr[i];
        var span = b.T - a.T;
        // A gap (ship out of view) is not interpolated across: hold the last point.
        if (span > MaxGap) { (x, z, yaw) = (a.X, a.Z, a.Yaw); return true; }
        var f = span <= 0 ? 0 : (t - a.T) / span;
        x = a.X + (b.X - a.X) * f;
        z = a.Z + (b.Z - a.Z) * f;
        var dy = b.Yaw - a.Yaw;
        while (dy > MathF.PI) dy -= 2 * MathF.PI;
        while (dy < -MathF.PI) dy += 2 * MathF.PI;
        yaw = a.Yaw + dy * f;
        return true;
    }

    public float HealthAt(float t)
    {
        var h = MaxHealth;
        foreach (var p in Health) { if (p.T <= t) h = p.Value; else break; }
        return IsDead(t) ? 0 : h;
    }

    /// <summary>First index with T &gt; t.</summary>
    private static int Lower(TrackPoint[] tr, float t)
    {
        int lo = 0, hi = tr.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tr[mid].T <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

public sealed record SceneKill(float T, string Victim, int VictimRelation, string Killer, int KillerRelation, string VictimShip);

/// <summary>Everything the minimap draws for one replay.</summary>
public sealed class ReplayScene
{
    public required ReplayData Data { get; init; }
    /// <summary>The playable area the minimap images cover; every position is placed through it.</summary>
    public required MapBounds Bounds { get; init; }

    /// <summary>The map's folder in the game files, e.g. "spaces/28_naval_mission".</summary>
    public string Space { get; init; } = "";
    public ImageSource? Water { get; init; }
    public ImageSource? Land { get; init; }
    public required IReadOnlyList<SceneShip> Ships { get; init; }
    public required IReadOnlyList<SceneKill> Kills { get; init; }

    public static ReplayScene Build(ReplayData data, MapAssets map, Func<long, (string Name, string Species, bool IsTest)?> ship)
    {
        var deaths = data.Deaths.GroupBy(d => d.Victim).ToDictionary(g => g.Key, g => g.First());
        string NameOf(int vehicle) => data.ByVehicle(vehicle) is { } p ? Services.Anonymizer.Name(p.Name) : "?";
        var ships = data.Players.Select(p =>
        {
            var info = ship(p.ShipId);
            deaths.TryGetValue(p.VehicleId, out var death);
            var hp = data.Health.GetValueOrDefault(p.VehicleId)?.ToArray() ?? [];
            return new SceneShip
            {
                Player = p,
                ShipName = info?.Name ?? $"Ship {p.ShipId}",
                Species = info?.Species ?? "",
                IsTestShip = info?.IsTest == true,
                Track = data.Tracks.GetValueOrDefault(p.VehicleId)?.ToArray() ?? [],
                Seen = data.Seen.GetValueOrDefault(p.VehicleId)?.ToArray() ?? [],
                Health = hp,
                MaxHealth = data.MaxHealth.TryGetValue(p.VehicleId, out var mh) ? mh : hp.Length > 0 ? hp.Max(h => h.Value) : 0,
                DeathAt = death?.T,
                KilledBy = death is null ? null : NameOf(death.Killer),
            };
        }).ToList();
        var byVehicle = ships.Where(s => s.Player.VehicleId != 0).ToDictionary(s => s.Player.VehicleId);
        var kills = data.Deaths.Select(d =>
        {
            byVehicle.TryGetValue(d.Victim, out var v);
            byVehicle.TryGetValue(d.Killer, out var k);
            return new SceneKill(d.T, v is null ? "?" : Services.Anonymizer.Name(v.Player.Name), v?.Relation ?? -1,
                k is null ? "?" : Services.Anonymizer.Name(k.Player.Name), k?.Relation ?? -1, v?.ShipName ?? "");
        }).ToList();
        return new ReplayScene
        {
            Data = data,
            Bounds = map.Bounds,
            Space = map.Space,
            Water = Image(map.Water),
            Land = Image(map.Land),
            Ships = ships,
            Kills = kills,
        };
    }

    private static BitmapImage? Image(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Game clock (seconds left) at replay time t, or null when the replay carries none.</summary>
    public float? ClockAt(float t)
    {
        float? v = null;
        foreach (var p in Data.TimeLeft) { if (p.T <= t) v = p.Value; else break; }
        return v;
    }
}
