using System.Net;
using System.Net.Sockets;

namespace PowerOrchestrator.Core.Power;

/// <summary>
/// Wake-on-LAN magic packet — the C# port of src/Proxmox/wake-node.sh's sender. A magic
/// packet is 6 bytes of 0xFF followed by the target's 6-byte MAC repeated 16 times,
/// broadcast over UDP. Proven on desktop-01 (wakes cleanly from S5).
/// </summary>
public static class WakeOnLan
{
    /// <summary>Parse a MAC (accepts <c>:</c> or <c>-</c> separators) into its 6 bytes.</summary>
    public static byte[] ParseMac(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        var hex = mac.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12)
            throw new FormatException($"'{mac}' is not a 48-bit MAC (expected 12 hex digits).");

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>Build the 102-byte magic packet for the given MAC.</summary>
    public static byte[] BuildMagicPacket(string mac)
    {
        var mb = ParseMac(mac);
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var rep = 1; rep <= 16; rep++) Array.Copy(mb, 0, packet, rep * 6, 6);
        return packet;
    }

    /// <summary>Broadcast a magic packet to wake the device with the given MAC.</summary>
    public static async Task SendAsync(
        string mac, string broadcast = "255.255.255.255", int port = 9, CancellationToken ct = default)
    {
        var packet = BuildMagicPacket(mac);
        using var udp = new UdpClient { EnableBroadcast = true };
        var endpoint = new IPEndPoint(IPAddress.Parse(broadcast), port);
        await udp.SendAsync(packet, packet.Length, endpoint).WaitAsync(ct).ConfigureAwait(false);
    }
}
