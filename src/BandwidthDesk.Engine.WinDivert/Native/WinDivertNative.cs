using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BandwidthDesk.Engine.WinDivert.Native;

/// <summary>
/// Minimal P/Invoke surface for WinDivert 2.x (https://reqrypt.org/windivert.html).
/// WinDivert.dll is loaded from the application directory; the signed driver
/// WinDivert64.sys is installed by WinDivert.dll on first open.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public enum Layer
    {
        Network = 0,
        NetworkForward = 1,
        Flow = 2,
        Socket = 3,
        Reflect = 4,
    }

    [Flags]
    public enum Flag : ulong
    {
        None = 0,
        Sniff = 0x0001,
        Drop = 0x0002,
        RecvOnly = 0x0004,
        ReadOnly = RecvOnly,
        SendOnly = 0x0008,
        WriteOnly = SendOnly,
        NoInstall = 0x0010,
        Fragments = 0x0020,
    }

    public enum Param
    {
        QueueLength = 0,
        QueueTime = 1,
        QueueSize = 2,
        VersionMajor = 3,
        VersionMinor = 4,
    }

    public enum ShutdownHow
    {
        Recv = 1,
        Send = 2,
        Both = 3,
    }

    /// <summary>
    /// WINDIVERT_ADDRESS — packet metadata. The struct layout is fixed at 80 bytes in WinDivert 2.x.
    /// Network.IfIdx/SubIfIdx live at the start of the layer-specific union.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
    public struct Address
    {
        public long Timestamp;        // QueryPerformanceCounter ticks
        public ulong LayerFlags;      // packed bitfields: layer:8 event:8 flags...
        public uint IfIdx;            // interface index (network layer)
        public uint SubIfIdx;         // sub-interface
        // remaining bytes for union content
        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
        public ulong Reserved4;
        public ulong Reserved5;
        public ulong Reserved6;

        public byte Layer => (byte)(LayerFlags & 0xFF);
        public byte Event => (byte)((LayerFlags >> 8) & 0xFF);
        public ulong Flags => LayerFlags >> 16;

        public bool Outbound
        {
            // Flag bits: Sniffed=0, Outbound=1, Loopback=2, Impostor=3, IPv6=4.
            get => (Flags & 0x0002UL) != 0;
            set
            {
                if (value) LayerFlags |= 1UL << 17;
                else LayerFlags &= ~(1UL << 17);
            }
        }

        public bool IsIPv6 => (Flags & 0x0010UL) != 0;
    }

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        Layer layer,
        short priority,
        Flag flags);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint readLen,
        ref Address address);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint sendLen,
        ref Address address);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        [In] byte[] packet,
        uint packetLen,
        out uint sendLen,
        ref Address address);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertShutdown(IntPtr handle, ShutdownHow how);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSetParam(IntPtr handle, Param param, ulong value);

    public static readonly IntPtr InvalidHandle = new(-1);
}
