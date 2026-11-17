using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace DiskHeartbeat;
public sealed class DriveIdentityException : IOException { public DriveIdentityException(string message) : base(message) { } }

// Disk numbers route IOCTLs and ETW events; Id deliberately retains the original NVMe hash format.
public record DriveInfo(string Id, string Path, string Model, long? Capacity, string Bus = "NVMe", string Firmware = "", bool? IsSsd = true)
{
    public int? DiskNumber => Path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) && int.TryParse(Path[17..], out int n) ? n : null;
    public override string ToString() => $"{Model} · {Bus} · {(Capacity is long c ? $"{c / 1e12:0.##} TB" : "capacity unavailable")}";
}
public record DriveTelemetry(decimal? HostBytes, string Source, decimal? PowerOnHours, string Detail);
public interface IDriveReader
{
    IReadOnlyList<DriveInfo> Enumerate();
    DriveTelemetry Read(DriveInfo drive);
    decimal ReadBytes(DriveInfo drive) => Read(drive).HostBytes ?? throw new IOException("Cumulative host writes unavailable");
}
public static class WindowsStorage
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlap);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDevice(string? name, [Out] char[] target, int length);
    public static SafeFileHandle Open(string path, bool ata = false)
    {
        var h = CreateFile(path, ata ? 0xC0000000 : 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid && !ata) { h.Dispose(); h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); }
        if (h.IsInvalid) { int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        return h;
    }
    public static int Io(SafeFileHandle h, uint code, byte[] input, byte[] output)
    {
        if (!DeviceIoControl(h, code, input, input.Length, output, output.Length, out int n, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return n;
    }
    public static uint U(byte[] b, int offset) => BitConverter.ToUInt32(b, offset);
    public static void Put(byte[] b, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(b, offset);
    public static string StableId(string model, string serial)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serial)) throw new IOException("Stable model/serial unavailable; history cannot be safely assigned");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Trim() + "\0" + serial.Trim())));
    }
    public static (string Model, string Serial, string Firmware, uint Bus) Descriptor(SafeFileHandle h)
    {
        var b = new byte[4096]; int n = Io(h, 0x2D1400, new byte[12], b);
        if (n < 36) throw new IOException("Truncated device descriptor");
        string Field(int o) { int p = checked((int)U(b, o)); if (p == 0) return ""; if (p < 36 || p >= n) throw new IOException("Invalid device string offset"); int end = Array.IndexOf(b, (byte)0, p, n - p); if (end < 0) throw new IOException("Unterminated device string"); return Encoding.ASCII.GetString(b, p, end - p).Trim(); }
        return (Field(16), Field(24), Field(20), U(b, 28));
    }
    public static void Revalidate(SafeFileHandle h, DriveInfo drive)
    {
        var d = Descriptor(h);
        if (StableId(d.Model, d.Serial) != drive.Id || (drive.Bus == "NVMe" ? d.Bus != 17 : d.Bus is not (11 or 3))) throw new DriveIdentityException("Drive identity changed; refresh drives before continuing");
    }
    public static string Error(Exception ex) => ex is Win32Exception w ? w.NativeErrorCode switch
    {
        5 => "Access denied: ATA requires a read/write device handle; run as administrator",
        1 or 50 or 87 => "Controller/driver does not support this native telemetry request (Windows error " + w.NativeErrorCode + ")",
        _ => $"Windows error {w.NativeErrorCode}: {w.Message}"
    } : ex.Message;
    public static IReadOnlyList<DriveInfo> Enumerate(out string diagnostics)
    {
        var names = new char[65536]; if (QueryDosDevice(null, names, names.Length) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<DriveInfo>(); var notes = new List<string>();
        foreach (var name in new string(names).Split('\0').Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^PhysicalDrive\d+$")).OrderBy(n => n))
        {
            try
            {
                string path = @"\\.\" + name; using var h = Open(path); var d = Descriptor(h);
                string bus = d.Bus switch { 17 => "NVMe", 11 => "SATA", 3 => "ATA/SATA", 7 => "USB", 8 => "RAID", 1 => "SCSI", _ => $"bus {d.Bus}" };
                if (d.Bus is not (17 or 11 or 3)) { notes.Add($"{name}: {d.Model} · {bus} · unsupported transport"); continue; }
                bool? ssd = d.Bus == 17 ? true : null;
                if (d.Bus != 17)
                {
                    try { var q = new byte[12]; Put(q, 0, 7); var seek = new byte[12]; if (Io(h, 0x2D1400, q, seek) >= 9) ssd = seek[8] == 0; } catch (Win32Exception) { }
                    if (AtaMappings.Find(d.Model, d.Firmware) != null) ssd = true;
                    if (ssd == false) { notes.Add($"{name}: {d.Model} · {bus} · rotational media skipped"); continue; }
                }
                long? capacity = null;
                try { var b = new byte[8]; if (Io(h, 0x7405C, Array.Empty<byte>(), b) >= 8) capacity = BitConverter.ToInt64(b); } catch (Win32Exception) { }
                if (capacity == null) try { var b = new byte[256]; if (Io(h, 0x700A0, Array.Empty<byte>(), b) >= 32) capacity = BitConverter.ToInt64(b, 24); } catch (Win32Exception) { }
                result.Add(new(StableId(d.Model, d.Serial), path, d.Model, capacity, d.Bus == 17 ? "NVMe" : "SATA", d.Firmware, ssd));
                notes.Add($"{name}: {d.Model} · {bus} · available" + (ssd == null ? " (SSD type unconfirmed)" : ""));
            }
            catch (Exception ex) { notes.Add($"{name}: {Error(ex)}"); }
        }
        // Quarantine colliding identifiers rather than prevent unrelated drives from being shown.
        var duplicate = result.GroupBy(d => d.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var drive in result.Where(d => duplicate.Contains(d.Id))) notes.Add($"{drive.Path}: duplicate stable identity; excluded");
        result.RemoveAll(d => duplicate.Contains(d.Id)); diagnostics = string.Join("\n", notes); return result;
    }
}
