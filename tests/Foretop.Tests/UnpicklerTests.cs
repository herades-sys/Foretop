using System.Numerics;
using Foretop.Core.GameData;

namespace Foretop.Tests;

public class UnpicklerTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("gp_proto0.pickle")]
    [InlineData("gp_proto2.pickle")]
    [InlineData("gp_proto4.pickle")]
    public void Reads_gameparams_shaped_objects(string file)
    {
        var root = Assert.IsType<Dictionary<object, object?>>(Unpickler.Load(File.ReadAllBytes(Fixture(file))));

        var ship = Assert.IsType<PyObject>(root["PJSB018_Yamato_1944"]);
        Assert.Equal("GPData", ship.Type.Name);
        Assert.Equal("PJSB018", ship["index"]);
        Assert.Equal(10L, ship["level"]);

        var typeinfo = Assert.IsType<PyObject>(ship["typeinfo"]);
        Assert.Equal("Battleship", typeinfo["species"]);

        var hull = Assert.IsType<PyObject>(ship["A_Hull"]);
        Assert.Equal(97200L, hull["health"]);
        Assert.Equal(27.0, hull["maxSpeed"]);
        var armor = Assert.IsType<Dictionary<object, object?>>(hull["armor"]);
        Assert.Equal(410.0, armor[65537L]);

        Assert.Equal(new List<object?> { "PJPA001", "PJPA002" }, ship["ammoList"]);
        Assert.Equal(BigInteger.Pow(2, 70), ship["big"]);
        Assert.Equal(-5L, ship["neg"]);
        Assert.Equal(new object?[] { 1L, 2.5, "x" }, ship["t"]);
        Assert.Equal(true, ship["flag"]);
        Assert.Null(ship["none"]);
        Assert.Equal("Kōtetsu", ship["uni"]);

        var nested = Assert.IsType<Dictionary<object, object?>>(ship["nested"]);
        var list = Assert.IsType<List<object?>>(nested["a"]);
        Assert.Equal(new object?[] { 3L }, Assert.IsType<Dictionary<object, object?>>(list[2])["b"]);

        // The same object referenced twice comes back as the same instance (memo).
        var shared = Assert.IsType<List<object?>>(root["shared"]);
        Assert.Same(shared[0], shared[1]);
        Assert.Same(ship, shared[0]);

        var proj = Assert.IsType<PyObject>(root["PJPA001_460mm_AP"]);
        Assert.Equal(14800.0, proj["alphaDamage"]);
    }

    [Fact]
    public void Long1_values_that_fit_a_long_come_back_as_long()
    {
        // Yamato's ship id, pickled with protocol 2 (LONG1, 5 bytes). Was boxed as BigInteger, which made every ship id 0.
        var v = Unpickler.Load([0x80, 0x02, 0x8a, 0x05, 0xD0, 0x36, 0xDF, 0xFE, 0x00, 0x2E]);
        Assert.Equal(4276041424L, Assert.IsType<long>(v));
    }

    [Fact]
    public void Unknown_opcodes_fail_clearly()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Unpickler.Load(new byte[] { 0x80, 0x02, 0xFF }));
        Assert.Contains("0xFF", ex.Message);
    }

    [Fact]
    public void Truncated_data_fails()
    {
        var data = File.ReadAllBytes(Fixture("gp_proto2.pickle"));
        Assert.ThrowsAny<Exception>(() => Unpickler.Load(data[..(data.Length / 2)]));
    }
}
