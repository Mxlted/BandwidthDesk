using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace BandwidthDesk.Core.Processes;

/// <summary>
/// Wraps IP Helper API (iphlpapi.dll) GetExtendedTcpTable / GetExtendedUdpTable.
/// Used to resolve a (protocol, localAddr, localPort) tuple to an owning PID.
///
/// The tables are snapshots, so the engine queries them on a cadence and caches results.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConnectionTable
{
    private const int AF_INET = 2;

    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;   // high-word zero; low-word port (network byte order in the high byte of low word)
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    public readonly record struct ConnectionKey(ProtocolType Protocol, IPAddress LocalAddress, int LocalPort);

    /// <summary>
    /// Snapshots current TCP+UDP IPv4 connections and returns a (proto, localAddr, localPort) -> pid map.
    /// IPv6 is intentionally omitted for the current engine.
    /// </summary>
    public static Dictionary<ConnectionKey, int> Snapshot()
    {
        var map = new Dictionary<ConnectionKey, int>(256);
        try
        {
            FillTcp(map);
            FillUdp(map);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read connection table");
        }
        return map;
    }

    private static void FillTcp(Dictionary<ConnectionKey, int> map)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) return;

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            IntPtr cursor = IntPtr.Add(buf, 4);
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(cursor);
                var addr = new IPAddress(row.localAddr);
                var port = NtoHsPort(row.localPort);
                map[new ConnectionKey(ProtocolType.Tcp, addr, port)] = (int)row.owningPid;
                cursor = IntPtr.Add(cursor, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void FillUdp(Dictionary<ConnectionKey, int> map)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (rc != 0) return;

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            IntPtr cursor = IntPtr.Add(buf, 4);
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(cursor);
                var addr = new IPAddress(row.localAddr);
                var port = NtoHsPort(row.localPort);
                map[new ConnectionKey(ProtocolType.Udp, addr, port)] = (int)row.owningPid;
                cursor = IntPtr.Add(cursor, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// MIB rows store the port in the low 16 bits of a uint, in network byte order. Convert to host order.
    /// </summary>
    private static int NtoHsPort(uint port)
    {
        // Low byte = MSB of port, next byte = LSB.
        return ((int)(port & 0xFF) << 8) | (int)((port >> 8) & 0xFF);
    }
}
