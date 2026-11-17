using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DiskHeartbeat;

public enum AtaTelemetryCommand { Identify, DeviceStatistics, SmartReadData }
public interface IAtaTransport { byte[] Read(AtaTelemetryCommand command); }
public sealed class AtaCommandException : IOException { public AtaCommandException(string message) : base(message) { } }
// The only requests constructible by this transport are read-only, 512-byte PIO telemetry.
public static class AtaPassThrough
{
    public static int HeaderSize => IntPtr.Size == 8 ? 48 : 40;
    static int PointerOffset => IntPtr.Size == 8 ? 24 : 20;
    static int PreviousOffset => IntPtr.Size == 8 ? 32 : 24;
    public static int TaskOffset => PreviousOffset + 8;
    public static byte[] Request(AtaTelemetryCommand command)
    {
        var b = new byte[HeaderSize + 512]; BitConverter.GetBytes((ushort)HeaderSize).CopyTo(b, 0);
        ushort flags = 3; // DRDY_REQUIRED | DATA_IN; no DMA, DATA_OUT, or reset.
        if (command == AtaTelemetryCommand.DeviceStatistics) flags |= 0x10;
        BitConverter.GetBytes(flags).CopyTo(b, 2); WindowsStorage.Put(b, 8, 512); WindowsStorage.Put(b, 12, 5);
        if (IntPtr.Size == 8) BitConverter.GetBytes((ulong)HeaderSize).CopyTo(b, PointerOffset); else WindowsStorage.Put(b, PointerOffset, (uint)HeaderSize);
        int t = TaskOffset; b[t + 1] = 1; b[t + 5] = 0xA0;
        switch (command)
        {
            case AtaTelemetryCommand.Identify: b[t + 6] = 0xEC; break;
            case AtaTelemetryCommand.DeviceStatistics: b[t + 2] = 4; b[t + 3] = 1; b[t + 5] = 0x40; b[t + 6] = 0x2F; break;
            case AtaTelemetryCommand.SmartReadData: b[t] = 0xD0; b[t + 3] = 0x4F; b[t + 4] = 0xC2; b[t + 6] = 0xB0; break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        return b;
    }
    public static byte[] Response(byte[] b, int returned)
    {
        if (returned < HeaderSize || returned > b.Length || b.Length < HeaderSize) throw new IOException("Truncated ATA pass-through header");
        if (BitConverter.ToUInt16(b, 0) != HeaderSize) throw new IOException("Invalid ATA pass-through header length");
        byte status = b[TaskOffset + 6];
        // BSY, DF, DRQ and ERR must be clear on completed PIO data-in. DRDY must be set.
        if ((status & 0xA9) != 0 || (status & 0x40) == 0) throw new AtaCommandException($"ATA telemetry failed: status 0x{status:X2}, error 0x{b[TaskOffset]:X2} (unsupported command, disabled telemetry, or controller limitation)");
        ulong offset = IntPtr.Size == 8 ? BitConverter.ToUInt64(b, PointerOffset) : WindowsStorage.U(b, PointerOffset);
        if (offset != (ulong)HeaderSize || WindowsStorage.U(b, 8) != 512 || offset + 512 > (ulong)returned) throw new IOException("Truncated or malformed ATA data transfer");
        return b.AsSpan(HeaderSize, 512).ToArray();
    }
}
public sealed class NativeAtaTransport : IAtaTransport
{
    readonly SafeFileHandle handle;
    public NativeAtaTransport(SafeFileHandle handle) => this.handle = handle;
    public byte[] Read(AtaTelemetryCommand command)
    {
        var b = AtaPassThrough.Request(command);
        return AtaPassThrough.Response(b, WindowsStorage.Io(handle, 0x4D02C, b, b));
    }
}
public sealed class AtaReader : IDriveReader
{
    public IReadOnlyList<DriveInfo> Enumerate() => WindowsStorage.Enumerate(out _).Where(d => d.Bus == "SATA").ToArray();
    public DriveTelemetry Read(DriveInfo drive)
    {
        using var h = WindowsStorage.Open(drive.Path, true); WindowsStorage.Revalidate(h, drive);
        return AtaTelemetry.Read(new NativeAtaTransport(h), drive);
    }
    public decimal ReadBytes(DriveInfo drive) => Read(drive).HostBytes ?? throw new IOException("ATA host-write counter unavailable");
}
