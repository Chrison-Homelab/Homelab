using PowerOrchestrator.Core.Power;
using Xunit;

namespace PowerOrchestrator.Tests;

public sealed class WakeOnLanTests
{
    [Fact]
    public void MagicPacket_has_correct_layout()
    {
        var packet = WakeOnLan.BuildMagicPacket("18:c0:4d:de:9f:82");

        Assert.Equal(102, packet.Length);                 // 6 + 16*6
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b)); // 6-byte sync stream

        var mac = new byte[] { 0x18, 0xc0, 0x4d, 0xde, 0x9f, 0x82 };
        for (var rep = 1; rep <= 16; rep++)
            Assert.Equal(mac, packet[(rep * 6)..(rep * 6 + 6)]);
    }

    [Theory]
    [InlineData("18:c0:4d:de:9f:82")]
    [InlineData("18-c0-4d-de-9f-82")]
    [InlineData("18C04DDE9F82")]
    public void ParseMac_accepts_common_separators(string mac)
    {
        var bytes = WakeOnLan.ParseMac(mac);
        Assert.Equal(new byte[] { 0x18, 0xc0, 0x4d, 0xde, 0x9f, 0x82 }, bytes);
    }

    [Theory]
    [InlineData("not-a-mac")]
    [InlineData("18:c0:4d:de:9f")]      // too short
    [InlineData("18:c0:4d:de:9f:82:00")] // too long
    public void ParseMac_rejects_bad_input(string mac)
    {
        Assert.Throws<FormatException>(() => WakeOnLan.ParseMac(mac));
    }
}
