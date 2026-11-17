using System.IO;
using System.Numerics;
using System.Collections.Concurrent;
namespace DiskHeartbeat;
public sealed class NvmeReader : IDriveReader
{
    public const string CounterSource = "nvme-duw-512000-v1";
    public string Diagnostics { get; private set; } = "";
    public IReadOnlyList<DriveInfo> Enumerate() { var all = WindowsStorage.Enumerate(out var notes); Diagnostics = notes; return all.Where(d => d.Bus == "NVMe").ToArray(); }
    public DriveTelemetry Read(DriveInfo drive)
    {
        using var h = WindowsStorage.Open(drive.Path); WindowsStorage.Revalidate(h, drive);
        var b = new byte[560]; WindowsStorage.Put(b, 0, 49); WindowsStorage.Put(b, 8, 3); WindowsStorage.Put(b, 12, 2); WindowsStorage.Put(b, 16, 2); WindowsStorage.Put(b, 24, 40); WindowsStorage.Put(b, 28, 512);
        int n = WindowsStorage.Io(h, 0x2D1400, b, b);
        if (n < 48 || WindowsStorage.U(b, 0) != 48 || WindowsStorage.U(b, 4) != 48 || WindowsStorage.U(b, 8) != 3 || WindowsStorage.U(b, 12) != 2) throw new IOException("Invalid NVMe protocol descriptor");
        uint offset = WindowsStorage.U(b, 24), length = WindowsStorage.U(b, 28);
        if (offset < 40 || length < 512 || (ulong)8 + offset + length > (ulong)n) throw new IOException("Truncated SMART log");
        var units = new BigInteger(b.AsSpan(checked(8 + (int)offset + 48), 16), true, false);
        if (units.IsZero) throw new IOException("NVMe Data Units Written is unreported (zero)");
        var hours = new BigInteger(b.AsSpan(checked(8 + (int)offset + 128), 16), true, false);
        return new(checked((decimal)units * 512000m), CounterSource, hours <= new BigInteger(decimal.MaxValue) ? (decimal)hours : null, "NVMe SMART Data Units Written × 512,000 bytes");
    }
    public decimal ReadBytes(DriveInfo d) => Read(d).HostBytes!.Value;
}
public sealed class NativeDriveReader : IDriveReader
{
    readonly NvmeReader nvme = new(); readonly AtaReader ata = new();
    readonly ConcurrentDictionary<string, object> gates = new();
    public string Diagnostics { get; private set; } = "";
    public IReadOnlyList<DriveInfo> Enumerate() { var all = WindowsStorage.Enumerate(out var notes); Diagnostics = notes; return all; }
    public DriveTelemetry Read(DriveInfo drive) { lock (gates.GetOrAdd(drive.Id, _ => new())) return drive.Bus == "NVMe" ? nvme.Read(drive) : ata.Read(drive); }
    public decimal ReadBytes(DriveInfo d) => Read(d).HostBytes ?? throw new IOException("Lifetime counter unavailable");
}
