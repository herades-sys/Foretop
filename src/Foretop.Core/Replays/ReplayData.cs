namespace Foretop.Core.Replays;

/// <summary>One ship in the battle. Relation: 0 = the recording player, 1 = ally, 2 = enemy (as in the arena info).</summary>
public sealed record ReplayPlayer(string Name, long ShipId, int Relation, int VehicleId, long AccountId, string? ClanTag)
{
    public bool IsSelf => Relation == 0;
    public bool IsEnemy => Relation == 2;
}

/// <summary>Position in world units (X east, Z north) and heading in radians.</summary>
public readonly record struct TrackPoint(float T, float X, float Z, float Yaw);

public readonly record struct ValuePoint(float T, float Value);

/// <summary>A span in which the recording client saw the ship (it is sent only while spotted or near).</summary>
public readonly record struct Visibility(float From, float To);

public sealed record ReplayDeath(float T, int Victim, int Killer, int Reason);

/// <summary>A capture point; Label is the in-game letter from its control-point index ("" for other zone kinds).</summary>
public sealed record ReplayZone(int EntityId, float X, float Z, float Radius, string Label, IReadOnlyList<(float T, int Team)> Owners, float From, float To)
{
    public int OwnerAt(float t)
    {
        var team = -1;
        foreach (var (ot, tm) in Owners) { if (ot <= t) team = tm; else break; }
        return team;
    }
}

public sealed record ReplaySmoke(int EntityId, float From, float To, float Radius, IReadOnlyList<(float X, float Z)> Points);

public sealed class ReplayData
{
    public required string MapSpace { get; init; }          // "spaces/28_naval_mission"
    public required string MapDisplayName { get; init; }    // "28_naval_mission"
    public required string PlayerName { get; init; }
    public required string PlayerVehicle { get; init; }
    public required string MatchGroup { get; init; }
    public required string Scenario { get; init; }
    public DateTime? StartedAt { get; init; }
    public string? ClientVersion { get; init; }
    public float Duration { get; init; }

    public required IReadOnlyList<ReplayPlayer> Players { get; init; }

    /// <summary>Team id of the recording player (capture point owners use team ids); -1 when unknown.</summary>
    public int SelfTeam { get; init; } = -1;
    public required IReadOnlyDictionary<int, List<TrackPoint>> Tracks { get; init; }
    public required IReadOnlyDictionary<int, List<Visibility>> Seen { get; init; }
    public required IReadOnlyDictionary<int, List<ValuePoint>> Health { get; init; }
    public required IReadOnlyDictionary<int, float> MaxHealth { get; init; }
    public required IReadOnlyList<ReplayDeath> Deaths { get; init; }
    public required IReadOnlyList<ReplayZone> Zones { get; init; }
    public required IReadOnlyList<ReplaySmoke> Smokes { get; init; }

    /// <summary>In-game clock (seconds left) by replay time; empty when the replay did not carry it.</summary>
    public required IReadOnlyList<ValuePoint> TimeLeft { get; init; }

    /// <summary>Things the decoder could not read; the rest of the replay is still shown.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public ReplayPlayer? Self => Players.FirstOrDefault(p => p.IsSelf);
    public ReplayPlayer? ByVehicle(int id) => Players.FirstOrDefault(p => p.VehicleId == id);
}
