using System;
using System.Net;
using System.Net.Sockets;

namespace BandwidthDesk.Engine.WinDivert.Native;

/// <summary>
/// Extracts the local-ownership tuple from IPv4/IPv6 TCP and UDP packets.
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
    /// Parses an IPv4 or IPv6 packet header and the L4 source/destination ports for TCP/UDP.
    /// Returns IsValid=false for malformed packets, non-initial fragments, or unsupported protocols.
    /// </summary>
    public static Parsed Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return default;

        return (packet[0] >> 4) switch
        {
            4 => ParseIPv4(packet),
            6 => ParseIPv6(packet),
            _ => default,
        };
    }

    private static Parsed ParseIPv4(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20) return default;

        byte versionIhl = packet[0];
        int ihl = (versionIhl & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl) return default;

        int totalLen = (packet[2] << 8) | packet[3];
        if (totalLen < ihl || totalLen > packet.Length) return default;

        byte protocol = packet[9];
        var src = new IPAddress(packet.Slice(12, 4).ToArray());
        var dst = new IPAddress(packet.Slice(16, 4).ToArray());

        return ParseTransport(packet, protocol, ihl, totalLen, src, dst);
    }

    private static Parsed ParseIPv6(ReadOnlySpan<byte> packet)
    {
        const int IPv6HeaderLength = 40;
        if (packet.Length < IPv6HeaderLength) return default;

        int payloadLength = (packet[4] << 8) | packet[5];
        int totalLength = payloadLength == 0 ? packet.Length : IPv6HeaderLength + payloadLength;
        if (totalLength < IPv6HeaderLength || totalLength > packet.Length) return default;

        byte nextHeader = packet[6];
        int transportOffset = IPv6HeaderLength;

        // Walk the extension headers that can precede TCP/UDP. WinDivert normally returns
        // reassembled inbound packets, but outbound/non-initial fragments still cannot be
        // associated with a socket because they do not contain transport ports.
        for (int extensions = 0; extensions < 8; extensions++)
        {
            if (nextHeader is 6 or 17)
                break;

            if (transportOffset + 2 > totalLength)
                return default;

            int extensionLength;
            switch (nextHeader)
            {
                // Hop-by-Hop Options, Routing, Destination Options, Mobility.
                case 0:
                case 43:
                case 60:
                case 135:
                    nextHeader = packet[transportOffset];
                    extensionLength = (packet[transportOffset + 1] + 1) * 8;
                    break;

                // Fragment header. Only the first fragment contains the transport ports.
                case 44:
                    if (transportOffset + 8 > totalLength)
                        return default;
                    int fragmentOffset = ((packet[transportOffset + 2] << 8) | packet[transportOffset + 3]) & 0xFFF8;
                    if (fragmentOffset != 0)
                        return default;
                    nextHeader = packet[transportOffset];
                    extensionLength = 8;
                    break;

                // Authentication Header length is expressed in 32-bit words, minus two.
                case 51:
                    nextHeader = packet[transportOffset];
                    extensionLength = (packet[transportOffset + 1] + 2) * 4;
                    break;

                default:
                    return default;
            }

            if (extensionLength <= 0 || transportOffset + extensionLength > totalLength)
                return default;

            transportOffset += extensionLength;
        }

        var src = new IPAddress(packet.Slice(8, 16));
        var dst = new IPAddress(packet.Slice(24, 16));
        return ParseTransport(packet, nextHeader, transportOffset, totalLength, src, dst);
    }

    private static Parsed ParseTransport(
        ReadOnlySpan<byte> packet,
        byte protocol,
        int transportOffset,
        int totalLength,
        IPAddress src,
        IPAddress dst)
    {
        ProtocolType protocolType = protocol switch
        {
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            _ => ProtocolType.Unknown,
        };

        if (protocolType == ProtocolType.Unknown || transportOffset + 4 > totalLength)
            return default;

        int srcPort = (packet[transportOffset] << 8) | packet[transportOffset + 1];
        int dstPort = (packet[transportOffset + 2] << 8) | packet[transportOffset + 3];

        return new Parsed(protocolType, src, dst, srcPort, dstPort, totalLength);
    }
}
