using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Foretop.Core.Replays;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Foretop.Tests;

public class ReplayTests
{
    // ------------------------------------------------------------------ fixtures

    private const string Alias = """
        <root>
          <ENTITY_ID>INT32</ENTITY_ID>
          <TEAM_ID>INT8</TEAM_ID>
          <PLAYER_ID>INT32</PLAYER_ID>
          <MAYBE>FIXED_DICT <Properties><a><Type>UINT8</Type></a></Properties> <AllowNone>true</AllowNone></MAYBE>
        </root>
        """;

    private static EntityDefs Defs() => EntityDefs.FromFiles(new Dictionary<string, string>
    {
        ["scripts/entities.xml"] = "<root><ClientServerEntities><Avatar/><Vehicle/></ClientServerEntities></root>",
        ["scripts/entity_defs/alias.xml"] = Alias,
        ["scripts/entity_defs/Avatar.def"] = """
            <root>
              <Implements><Interface>Chat</Interface></Implements>
              <ClientMethods>
                <onArenaStateReceived><Args>
                  <arenaUniqueId>INT64</arenaUniqueId><teamBuildTypeId>INT8</teamBuildTypeId>
                  <preBattlesInfo>BLOB</preBattlesInfo><playersStates>BLOB</playersStates>
                </Args></onArenaStateReceived>
                <receiveVehicleDeath><Arg>ENTITY_ID</Arg><Arg>ENTITY_ID</Arg><Arg>UINT32</Arg></receiveVehicleDeath>
                <onPing><Arg>UINT8</Arg></onPing>
              </ClientMethods>
            </root>
            """,
        ["scripts/entity_defs/interfaces/Chat.def"] = """
            <root><ClientMethods><onChatMessage><Arg>PLAYER_ID</Arg><Arg>STRING</Arg><Arg>STRING</Arg><Arg>STRING</Arg></onChatMessage><onTick><Arg>UINT32</Arg><Arg>UINT32</Arg><Arg>UINT32</Arg></onTick></ClientMethods></root>
            """,
        ["scripts/entity_defs/Vehicle.def"] = """
            <root>
              <Implements><Interface>Engine</Interface></Implements>
              <Properties>
                <health><Type>FLOAT32</Type><Flags>ALL_CLIENTS</Flags></health>
                <serverOnly><Type>UINT8</Type><Flags>CELL_PRIVATE</Flags></serverOnly>
                <teamId><Type>TEAM_ID</Type><Flags>ALL_CLIENTS</Flags></teamId>
                <maxHealth><Type>FLOAT32</Type><Flags>ALL_CLIENTS</Flags></maxHealth>
              </Properties>
            </root>
            """,
        ["scripts/entity_defs/interfaces/Engine.def"] = """
            <root><Properties><enginePower><Type>FLOAT32</Type><Flags>ALL_CLIENTS</Flags></enginePower></Properties></root>
            """,
    });

    // ------------------------------------------------------------------ definitions

    [Fact]
    public void Indices_follow_size_then_interfaces_before_own_definitions()
    {
        var defs = Defs();
        var vehicle = defs.Entity("Vehicle")!;
        // Size 1 first (teamId); then the 4-byte ones, the interface's enginePower before Vehicle's own; server-only skipped.
        Assert.Equal(["teamId", "enginePower", "health", "maxHealth"], vehicle.Properties.Select(p => p.Name));

        var avatar = defs.Entity("Avatar")!;
        Assert.Equal(["onPing", "onTick", "receiveVehicleDeath", "onChatMessage", "onArenaStateReceived"], avatar.ClientMethods.Select(m => m.Name));
        Assert.Equal(EntityDefs.Variable, avatar.ClientMethods[avatar.MethodIndex("onArenaStateReceived")].Size);
        Assert.Equal("Vehicle", defs.TypeName(2)); // type ids are 1-based
        Assert.Null(defs.TypeName(0));
    }

    [Fact]
    public void Values_decode_with_packed_lengths_and_nullable_dicts()
    {
        var defs = Defs();
        var data = new List<byte> { 0xFF, 3, 1, 0 }; // 24-bit length 259
        data.AddRange(Enumerable.Repeat((byte)'x', 259));
        data.Add(0);                                // MAYBE: None
        data.AddRange([1, 42]);                     // MAYBE: { a = 42 }
        var r = new BigWorldReader(data.ToArray(), defs);

        Assert.Equal(259, ((string)r.Read(XElement.Parse("<t>STRING</t>"))!).Length);
        Assert.Null(r.Read(XElement.Parse("<t>MAYBE</t>")));
        var d = (Dictionary<string, object?>)r.Read(XElement.Parse("<t>MAYBE</t>"))!;
        Assert.Equal(42L, d["a"]);
        Assert.Equal(0, r.Remaining);
    }

    // ------------------------------------------------------------------ file and packets

    private static readonly byte[] Key = [0x29, 0xB7, 0xC9, 0x09, 0x38, 0x3F, 0x84, 0x88, 0xFA, 0x98, 0xEC, 0x4E, 0x13, 0x19, 0x79, 0xFB];

    /// <summary>The inverse of the game's scheme: zlib, then Blowfish with each block XOR-ed with the previous plain block.</summary>
    private static byte[] BuildReplay(string arenaJson, byte[] packets, string block2 = "549692505.123")
    {
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(packets);
        var plain = z.ToArray();
        Array.Resize(ref plain, (plain.Length + 7) / 8 * 8);
        var bf = new BlowfishEngine();
        bf.Init(true, new KeyParameter(Key));
        var enc = new byte[plain.Length];
        var block = new byte[8];
        for (var i = 0; i < plain.Length; i += 8)
        {
            for (var k = 0; k < 8; k++) block[k] = (byte)(plain[i + k] ^ (i > 0 ? plain[i - 8 + k] : 0));
            bf.ProcessBlock(block, 0, enc, i);
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(ReplayFile.Magic);
        var blocks = new[] { arenaJson, "", block2 };
        w.Write(blocks.Length);
        foreach (var b in blocks) { var bytes = Encoding.UTF8.GetBytes(b); w.Write(bytes.Length); w.Write(bytes); }
        w.Write(packets.Length);
        w.Write(enc.Length);
        w.Write(enc);
        return ms.ToArray();
    }

    private sealed class Packets
    {
        private readonly MemoryStream _ms = new();
        private readonly BinaryWriter _w;
        public Packets() => _w = new BinaryWriter(_ms);

        public Packets Add(uint type, float t, Action<BinaryWriter> body)
        {
            var b = new MemoryStream();
            body(new BinaryWriter(b));
            _w.Write((int)b.Length);
            _w.Write(type);
            _w.Write(t);
            _w.Write(b.ToArray());
            return this;
        }

        public byte[] Bytes => _ms.ToArray();
    }

    private static void Vec(BinaryWriter w, float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }

    [Fact]
    public void Replay_file_round_trips()
    {
        var packets = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        var file = ReplayFile.Parse(BuildReplay("{\"x\":1}", packets));
        Assert.Equal("{\"x\":1}", file.ArenaJson);
        Assert.Equal(packets, file.Packets);
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
    }

    [Fact]
    public void Parser_builds_tracks_health_kills_chat_and_links_players()
    {
        var defs = Defs();
        var avatar = defs.Entity("Avatar")!;
        var vehicle = defs.Entity("Vehicle")!;
        const int me = 1001, foe = 1003, avatarId = 1000;
        // Pickled player list (protocol 0): [[(1, 'Bob'), (2, 1001), (3, 549692505), (4, 900001)], [(1, 'Eve'), (2, 1003), (3, 7), (4, 900002)]]
        // Key 4 is the id chat lines name as sender.
        var pickle = Encoding.ASCII.GetBytes(
            "(l(l(I1\nS'Bob'\nta(I2\nI1001\nta(I3\nI549692505\nta(I4\nI900001\ntaa(l(I1\nS'Eve'\nta(I2\nI1003\nta(I3\nI7\nta(I4\nI900002\ntaa.");
        void Chat(BinaryWriter w, int sender, string channel, string text)
        {
            var args = new MemoryStream();
            var a = new BinaryWriter(args);
            a.Write(sender);
            foreach (var str in new[] { channel, text, "" }) { var b = Encoding.UTF8.GetBytes(str); a.Write((byte)b.Length); a.Write(b); }
            w.Write(avatarId); w.Write(avatar.MethodIndex("onChatMessage")); w.Write((int)args.Length); w.Write(args.ToArray());
        }

        var p = new Packets()
            .Add(0x00, 0, w => { w.Write(avatarId); w.Write((short)1); })
            .Add(0x08, 0.1f, w =>
            {
                var args = new MemoryStream();
                var a = new BinaryWriter(args);
                a.Write(42L); a.Write((byte)0); a.Write((byte)0); a.Write((byte)pickle.Length); a.Write(pickle);
                w.Write(avatarId); w.Write(avatar.MethodIndex("onArenaStateReceived")); w.Write((int)args.Length); w.Write(args.ToArray());
            });
        foreach (var (id, team) in new[] { (me, 0), (foe, 1) })
            p.Add(0x05, 1, w =>
            {
                w.Write(id); w.Write((short)2); w.Write(0); w.Write(0); Vec(w, 0, 0, 0); Vec(w, 0, 0, 0);
                var st = new MemoryStream();
                var s = new BinaryWriter(st);
                s.Write((byte)2);
                s.Write((byte)vehicle.PropertyIndex("teamId")); s.Write((sbyte)team);
                s.Write((byte)vehicle.PropertyIndex("maxHealth")); s.Write(50_000f);
                w.Write((int)st.Length); w.Write(st.ToArray());
            });
        p.Add(0x2C, 2, w => { w.Write(me); w.Write(0); Vec(w, 10, 0, 20); w.Write(1.5f); w.Write(0f); w.Write(0f); })
         .Add(0x0A, 2, w => { w.Write(foe); w.Write(0); Vec(w, -100, 0, 50); Vec(w, 0, 0, 0); w.Write(0.5f); w.Write(0f); w.Write(0f); w.Write((byte)0); })
         .Add(0x07, 3, w => { w.Write(foe); w.Write(vehicle.PropertyIndex("health")); w.Write(4); w.Write(12_345f); })
         .Add(0x08, 4, w => { w.Write(avatarId); w.Write(avatar.MethodIndex("receiveVehicleDeath")); w.Write(12); w.Write(foe); w.Write(me); w.Write(17); })
         .Add(0x08, 4.5f, w => Chat(w, 900002, "battle_common", "gl hf"))
         .Add(0x08, 6, w => Chat(w, 900001, "battle_team", "wp"))
         .Add(0x04, 5, w => w.Write(foe))
         .Add(0x0A, 9, w => { w.Write(me); w.Write(0); Vec(w, 11, 0, 21); Vec(w, 0, 0, 0); w.Write(1.5f); w.Write(0f); w.Write(0f); w.Write((byte)0); });

        const string arena = """
            {"mapName":"spaces/28_naval_mission","mapDisplayName":"28_naval_mission","playerName":"Bob","playerVehicle":"X",
             "matchGroup":"pvp","scenario":"domination","dateTime":"15.11.2026 00:52:51",
             "vehicles":[{"name":"Bob","shipId":11,"relation":0,"id":5},{"name":"Eve","shipId":22,"relation":2,"id":6}]}
            """;
        var data = ReplayParser.Parse(ReplayFile.Parse(BuildReplay(arena, p.Bytes)), defs);

        Assert.Empty(data.Warnings);
        var bob = data.Players.Single(x => x.Name == "Bob");
        var eve = data.Players.Single(x => x.Name == "Eve");
        Assert.Equal(me, bob.VehicleId);
        Assert.Equal(foe, eve.VehicleId);
        Assert.Equal(549692505, bob.AccountId); // found through header block 2
        Assert.Equal(0, data.SelfTeam);
        Assert.Equal(2, data.Tracks[me].Count);
        Assert.Equal(-100, data.Tracks[foe][0].X);
        Assert.Equal(12_345f, data.Health[foe].Last().Value);
        Assert.Equal(50_000f, data.MaxHealth[foe]);
        var death = Assert.Single(data.Deaths);
        Assert.Equal((foe, me, 4f), (death.Victim, death.Killer, death.T));
        Assert.Equal(new Visibility(1, 5), Assert.Single(data.Seen[foe]));
        Assert.Equal(new Visibility(0, 9), Assert.Single(data.Seen[me])); // own ship: whole replay
        Assert.Equal(9, data.Duration);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 52, 51), data.StartedAt);

        Assert.Equal(2, data.Chat.Count);
        Assert.Equal((4.5f, "Eve", false, "gl hf"), (data.Chat[0].T, data.Chat[0].Sender?.Name, data.Chat[0].IsTeam, data.Chat[0].Text));
        Assert.Equal(("Bob", true, "wp"), (data.Chat[1].Sender?.Name, data.Chat[1].IsTeam, data.Chat[1].Text));
        Assert.Equal(900002, eve.AvatarId);
    }

    [Theory]
    [InlineData("<space.settings><chunkSize>100.0</chunkSize><bounds><minX>-9</minX><maxX>8</maxX></bounds></space.settings>", 700)]
    [InlineData("<space.settings><chunkSize>100.0</chunkSize><bounds><minX>-12</minX><maxX>11</maxX></bounds></space.settings>", 1000)]
    [InlineData("not xml", 700)]
    public void Playable_area_comes_from_the_chunk_bounds(string settings, float half) =>
        Assert.Equal(half, ReplayLibrary.PlayableHalf(settings));
}
