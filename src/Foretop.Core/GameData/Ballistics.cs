namespace Foretop.Core.GameData;

/// <summary>One point of a shell's flight, calculated.</summary>
public sealed record BallisticPoint(double RangeKm, double BeltPenetration, double DeckPenetration, double FlightTime, double FallAngle, double ImpactVelocity);

/// <summary>
/// Community ballistics model (jcw780/wows_shell, landaire/wows-toolkit): standard-atmosphere drag, RK4 at 0.02 s,
/// penetration P = 1e-7 · krupp · m^0.69 · d^-1.07 · v^1.38 (mm). "Belt" = P·cos(angle of fall), "deck" = P·sin.
/// Flight time is in game seconds (real time ÷ 2.75). These figures are calculated, not read from the game.
/// </summary>
public static class Ballistics
{
    private const double G = 9.8, T0 = 288.15, L = 0.0065, P0 = 101325, R = 8.31447, M = 0.0289644;
    private const double Dt = 0.02, TimeMultiplier = 2.75;
    public const double OvermatchRatio = 14.3;

    public static double RawPenetration(ShellData s, double velocity) =>
        s.Krupp <= 0 ? 0 : 1e-7 * s.Krupp * Math.Pow(s.Mass, 0.69) * Math.Pow(s.Diameter, -1.07) * Math.Pow(velocity, 1.38);

    /// <summary>Plates thinner than this are overmatched (calibre / 14.3).</summary>
    public static double OvermatchThreshold(double calibreMm) => calibreMm / OvermatchRatio;

    private static double AirDensity(double y)
    {
        var t = T0 - L * y;
        var p = P0 * Math.Pow(t / T0, G * M / (R * L));
        return M * p / (R * t);
    }

    /// <summary>Flies one shot at launch angle θ (radians); returns range (m), real flight time, impact velocity components.</summary>
    private static (double X, double T, double Vx, double Vy) Fly(ShellData s, double theta)
    {
        var k = 0.5 * s.AirDrag * Math.Pow(s.Diameter / 2, 2) * Math.PI / s.Mass;
        double x = 0, y = 0, vx = s.Velocity * Math.Cos(theta), vy = s.Velocity * Math.Sin(theta), t = 0;

        (double dx, double dy, double dvx, double dvy) D(double py, double pvx, double pvy)
        {
            var v = Math.Sqrt(pvx * pvx + pvy * pvy);
            var kr = k * AirDensity(Math.Max(0, py));
            return (pvx, pvy, -kr * pvx * v, -G - kr * pvy * v);
        }

        // Classic RK4 on (x, y, vx, vy).
        for (var step = 0; step < 200_000; step++)
        {
            var k1 = D(y, vx, vy);
            var k2 = D(y + Dt / 2 * k1.dy, vx + Dt / 2 * k1.dvx, vy + Dt / 2 * k1.dvy);
            var k3 = D(y + Dt / 2 * k2.dy, vx + Dt / 2 * k2.dvx, vy + Dt / 2 * k2.dvy);
            var k4 = D(y + Dt * k3.dy, vx + Dt * k3.dvx, vy + Dt * k3.dvy);
            var nx = x + Dt / 6 * (k1.dx + 2 * k2.dx + 2 * k3.dx + k4.dx);
            var ny = y + Dt / 6 * (k1.dy + 2 * k2.dy + 2 * k3.dy + k4.dy);
            var nvx = vx + Dt / 6 * (k1.dvx + 2 * k2.dvx + 2 * k3.dvx + k4.dvx);
            var nvy = vy + Dt / 6 * (k1.dvy + 2 * k2.dvy + 2 * k3.dvy + k4.dvy);

            if (ny < 0 && step > 0)
            {
                var f = y / (y - ny); // interpolate back to sea level
                return (x + (nx - x) * f, t + Dt * f, vx + (nvx - vx) * f, vy + (nvy - vy) * f);
            }
            (x, y, vx, vy, t) = (nx, ny, nvx, nvy, t + Dt);
        }
        return (x, t, vx, vy);
    }

    /// <summary>Solves the launch angle for a range (low arc) and returns penetration and flight figures, or null if out of reach.</summary>
    public static BallisticPoint? At(ShellData s, double rangeKm)
    {
        var target = rangeKm * 1000;
        double lo = 0.001 * Math.PI / 180, hi = 45 * Math.PI / 180;
        if (Fly(s, hi).X < target) return null;
        (double X, double T, double Vx, double Vy) shot = default;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            shot = Fly(s, mid);
            if (Math.Abs(shot.X - target) < 0.5) break;
            if (shot.X < target) lo = mid; else hi = mid;
        }
        var v = Math.Sqrt(shot.Vx * shot.Vx + shot.Vy * shot.Vy);
        var fall = Math.Atan(Math.Abs(shot.Vy) / shot.Vx);
        var p = RawPenetration(s, v);
        return new BallisticPoint(rangeKm, p * Math.Cos(fall), p * Math.Sin(fall), shot.T / TimeMultiplier, fall * 180 / Math.PI, v);
    }

    /// <summary>The design's table: 5, 10, 15 … km up to the maximum range, plus the maximum itself.</summary>
    public static List<BallisticPoint> Table(ShellData s, double maxRangeKm)
    {
        var list = new List<BallisticPoint>();
        for (var km = 5.0; km < maxRangeKm - 0.05; km += 5)
            if (At(s, km) is { } p) list.Add(p);
        if (At(s, Math.Round(maxRangeKm, 2)) is { } last) list.Add(last);
        return list;
    }
}
