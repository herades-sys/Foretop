using System.Text.Json;
using Foretop.Core.Replays;
using Foretop.Core.Stats;

namespace Foretop.Core.Personal;

/// <summary>
/// Reads the recording player's battle facts from a replay. Two kinds of data:
/// <list type="bullet">
/// <item>Decoded game messages (verified decoding): kills from the death messages, death time, battle timer → kills,
/// survival, time survived.</item>
/// <item>The game's result data (packet 0x22): one record of values per player whose meaning is known only by position
/// (the game does not name them). Positions checked on build 13533378: 0 = account id, 7 = ship id, 416 = base XP,
/// 438 = damage. They are used only when the anchors hold for this replay: account id and ship id at their places, and
/// the damage equal to the API's damage for the battle when that is known. Otherwise those values are left out.</item>
/// </list>
/// Nothing is estimated: what cannot be checked is missing, with a note.
/// </summary>
public static class ReplayFacts
{
    public const int PosAccount = 0, PosShip = 7, PosBaseXp = 416, PosDamage = 438;

    public sealed record Outcome(ReplayBattleFacts Facts, string? Note);

    public static Outcome Compute(string? resultsJson, long selfAccount, long selfShipId, int selfVehicle, int selfRelationTeam,
        IReadOnlyList<ReplayPlayer> players, IReadOnlyList<ReplayDeath> deaths, IReadOnlyList<ValuePoint> timeLeft, float duration,
        StatTotals? api)
    {
        string? note = null;

        // Battle timer: the countdown before the battle counts down from ~30 s; the battle timer then jumps to the
        // battle length (e.g. 1,200 s). The jump is the start of the battle.
        double? start = null;
        for (var i = 1; i < timeLeft.Count; i++)
            if (timeLeft[i].Value > timeLeft[i - 1].Value + 60) { start = timeLeft[i].T; break; }

        var death = deaths.FirstOrDefault(d => d.Victim == selfVehicle && selfVehicle != 0);
        int? kills = selfVehicle == 0 ? null : deaths.Count(d => d.Killer == selfVehicle && d.Victim != selfVehicle);
        bool? survived = selfVehicle == 0 ? null : death is null;
        double? life = start is double s0
            ? Math.Max(0, (death is not null ? death.T : duration) - s0)
            : null;
        double? length = start is double s1 ? Math.Max(0, duration - s1) : null;

        int? baseXp = null, position = null, teamSize = null;
        long? damage = null;
        if (resultsJson is null)
        {
            note = "the replay has no result data (the recording ended before the battle did)";
        }
        else if (Rows(resultsJson) is not { } rows)
        {
            note = "the replay's result data could not be read";
        }
        else if (!rows.TryGetValue(selfAccount, out var me) || Long(me, PosAccount) != selfAccount || Long(me, PosShip) != selfShipId)
        {
            note = "the replay's result data has a layout Foretop does not know (game update?); base XP and team position not read";
        }
        else
        {
            damage = Long(me, PosDamage);
            if (api is { Battles: 1 } a && damage != a.Damage)
            {
                note = $"replay damage {damage:N0} ≠ API damage {a.Damage:N0}: the result layout changed, base XP and team position not read";
                damage = null;
            }
            else
            {
                var xp = Long(me, PosBaseXp);
                // Base XP is before Premium Account and bonuses, so it cannot exceed the XP the API counted.
                if (xp is >= 0 and <= int.MaxValue && (api is not { Battles: 1 } b || (xp <= b.Xp && b.Xp <= xp * 5 + 50)))
                    baseXp = (int)xp;

                var team = players.Where(p => (p.Relation <= 1 ? 0 : 1) == selfRelationTeam && p.AccountId > 0)
                    .Select(p => rows.TryGetValue(p.AccountId, out var r) && Long(r, PosAccount) == p.AccountId ? Long(r, PosDamage) : (long?)null)
                    .Where(d => d is not null).Select(d => d!.Value).ToList();
                if (team.Count >= 2 && damage is long mine)
                {
                    position = 1 + team.Count(d => d > mine);
                    teamSize = team.Count;
                }
            }
        }

        return new Outcome(new ReplayBattleFacts
        {
            BaseXp = baseXp, Damage = damage, Kills = kills, Survived = survived, LifeTime = life, TeamPosition = position,
            TeamSize = teamSize, Duration = length,
        }, note);
    }

    /// <summary>playersPublicInfo: account id → that player's value record.</summary>
    private static Dictionary<long, JsonElement>? Rows(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("playersPublicInfo", out var info) || info.ValueKind != JsonValueKind.Object) return null;
            var rows = new Dictionary<long, JsonElement>();
            foreach (var p in info.EnumerateObject())
                if (long.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.Array) rows[id] = p.Value.Clone();
            return rows;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Long(JsonElement row, int index)
    {
        if (index >= row.GetArrayLength()) return null;
        var v = row[index];
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x
            : v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)Math.Round(d) : null;
    }
}
