using Foretop.Core.GameData;

namespace Foretop.Tests;

public class BallisticsTests
{
    // Yamato's 460 mm AP/APC Type91, values read from GameParams (build 13533378).
    private static readonly ShellData YamatoAp = new()
    {
        Name = "PJPA017_Shell_460mm_AP_APC_Type_91", DisplayName = "460 mm AP/APC Type91", Type = "AP",
        Damage = 14800, Velocity = 780, Mass = 1460, Diameter = 0.46, Krupp = 2574, AirDrag = 0.292, Capped = true,
    };

    [Fact]
    public void Muzzle_penetration_matches_reference() =>
        Assert.Equal(883.0, Ballistics.RawPenetration(YamatoAp, 780), 0);

    /// <summary>The design's "Penetration by range" table for Yamato (belt mm, game seconds, angle of fall).</summary>
    [Theory]
    [InlineData(5, 767, 2.5, 2.7)]
    [InlineData(10, 667, 5.2, 6.1)]
    [InlineData(15, 582, 8.2, 10.5)]
    [InlineData(20, 513, 11.6, 15.9)]
    [InlineData(25, 458, 15.4, 22.2)]
    [InlineData(26.63, 444, 16.7, 24.4)]
    public void Matches_design_table(double km, double belt, double time, double angle)
    {
        var p = Ballistics.At(YamatoAp, km)!;
        Assert.InRange(p.BeltPenetration, belt - 1.5, belt + 1.5);
        Assert.InRange(p.FlightTime, time - 0.1, time + 0.1);
        Assert.InRange(p.FallAngle, angle - 0.15, angle + 0.15);
    }

    [Fact]
    public void Out_of_reach_is_null() => Assert.Null(Ballistics.At(YamatoAp, 60));

    [Fact]
    public void Overmatch_threshold() => Assert.Equal(32.2, Ballistics.OvermatchThreshold(460), 1);
}
