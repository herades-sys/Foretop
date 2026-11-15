using System.Buffers.Binary;
using System.Numerics;
using Foretop.Core.GameData;

namespace Foretop.Tests;

public class GameDataTests
{
    /// <summary>A minimal .geometry file: one armour model with one node of one triangle.</summary>
    private static byte[] Geometry(int key, params Vector3[] vertices)
    {
        const int header = 0x48, proto = 0x20;
        var blob = 32 + 32 + vertices.Length * 16;
        var b = new byte[header + proto + blob];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x14), 1);              // armorModelCount
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(0x40), header);          // armorModelsPtr
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(header), (ulong)blob);  // sizeInBytes
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(header + 0x18), proto);  // dataPtr, relative to the prototype
        var data = header + proto;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(data + 32), (uint)key);                      // node header 0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(data + 48 + 12), (uint)vertices.Length);     // node header 1
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = data + 64 + i * 16;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), vertices[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), vertices[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 8), vertices[i].Z);
        }
        return b;
    }

    [Fact]
    public void Armour_geometry_reads_keys_and_triangles()
    {
        var key = (2 << 16) | 61; // layer 2, Cit_Belt
        var tris = ArmourGeometry.Parse(Geometry(key, new(0, 0, 0), new(1, 0, 0), new(0, 1, 2)));

        var t = Assert.Single(tris);
        Assert.Equal(key, t.Key);
        Assert.Equal(2, t.Layer);
        Assert.Equal(61, t.Material);
        Assert.Equal(new Vector3(0, 1, 2), t.C);
    }

    [Fact]
    public void Geometry_without_armour_gives_nothing()
    {
        Assert.Empty(ArmourGeometry.Parse(new byte[0x48]));
        Assert.Empty(ArmourGeometry.Parse(new byte[10]));
    }

    [Theory]
    [InlineData("GMShotDelay", 0.88, "Main battery reload time −12%")]
    [InlineData("SGRudderTime", 0.6, "Rudder shift time −40%")]
    [InlineData("AAExtraBubbles", 1L, null)]          // no curated label and no game texts in the test → left out
    [InlineData("visionXRayTorpedoDist", 1800L, null)]
    [InlineData("engineForwardUpTime", 0.5, "Time to reach full power, forward −50%")]
    [InlineData("GMShotDelay", 1.0, null)]            // changes nothing
    [InlineData("someUnknownCoeff", 1.1, null)]       // unknown key, no game text → not shown under its internal name
    public void Upgrade_modifiers_read_as_text(string key, object value, string? expected)
    {
        Assert.Equal(expected, ShipExtractor.ModifierText(key, value));
    }

    [Fact]
    public void Per_class_modifiers_group_equal_values()
    {
        var value = new Dictionary<object, object?> { ["Destroyer"] = 0.9, ["Cruiser"] = 0.9, ["Battleship"] = 1.0 };
        Assert.Equal("Surface detectability −10% (DD, CA)", ShipExtractor.ModifierText("visibilityDistCoeff", value));
    }

    [Theory]
    [InlineData("Cit_Belt", "Citadel")]
    [InlineData("Bow_ConstrSide", "Bow")]
    [InlineData("St_Deck", "Stern")]
    [InlineData("SS_Side", "Superstructure")]
    [InlineData("Tur1GkBar", "Turrets")]
    [InlineData("RudderSide", "Steering gear")]
    [InlineData("Bulge", "Torpedo protection")]
    public void Plate_zones_come_from_material_names(string material, string zone)
    {
        Assert.Equal(zone, ShipExtractor.Zone(material));
    }

    [Theory]
    [InlineData("superShip", "Supership", true)]
    [InlineData("specialUnsellable", "Premium", true)]
    [InlineData("event", "Event", false)]
    [InlineData("disabled", "Not obtainable", false)]
    public void Availability_groups(string group, string label, bool obtainable)
    {
        Assert.Equal(label, ShipExtractor.Availability(group));
        Assert.Equal(obtainable, ShipExtractor.IsObtainable(group));
    }
}
