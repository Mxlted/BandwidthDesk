using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BandwidthDesk.Engine.WinDivert.Native;

/// <summary>
/// Just enough IPv4 / TCP / UDP header parsing to extract (proto, srcAddr, srcPort, dstAddr, dstPort) and packet length.
/// </summary>
internal static class PacketHeaders
{
    public readonly ref struct Parsed
    {
        public readonly bool IsValid;
        public readonly ProtocolType Protocol;
        public readonly IPAddress SrcAddress;
        public readonly IPAddress DstAddress;
        public readonly int SrcPort;
        public readonly int DstPort;
        public readonly int TotalLength;

        public Parsed(ProtocolType protocol, IPAddress src, IPAddress dst, int srcPort, int dstPort, int totalLen)
        {
            IsValid = true;
            Protocol = protocol;
            SrcAddress = src;
            DstAddress = dst;
            SrcPort = srcPort;
            DstPort = dstPort;
            TotalLength = totalLen;
        }
    }

    /// <summary>
    /// Parses an IPv4 packet header and the L4 source/dest ports for TCP/UDP.
    /// Returns IsValid=false for non-IPv4 or unsupported protocols.
    /// </summary>
    public static Parsed Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20) return default;

        byte versionIhl = packet[0];
        int version = versionIhl >> 4;
        int ihl = (versionIhl & 0x0F) * 4;
        if (version != 4 || ihl < 20 || packet.Length < ihl) return default;

        int totalLen = (packet[2] << 8) | packet[3];
        if (totalLen < ihl || totalLen > packet.Length) return default;

        byte protocol = packet[9];
        var src = new IPAddress(packet.Slice(12, 4).ToArray());
        var dst = new IPAddress(packet.Slice(16, 4).ToArray());

        if (packet.Length < ihl + 4) return default;

        int srcPort = (packet[ihl] << 8) | packet[ihl + 1];
        int dstPort = (packet[ihl + 2] << 8) | packet[ihl + 3];

        ProtocolType pt = protocol switch
        {
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            _ => ProtocolType.Unknown,
        };
        if (pt == ProtocolType.Unknown) return default;

        return new Parsed(pt, src, dst, srcPort, dstPort, totalLen);
    }
}
