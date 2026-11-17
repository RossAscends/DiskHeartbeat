using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace DiskHeartbeat;

public record AtaIdentity(string Model, string Serial, string Firmware, int LogicalSectorBytes, bool? IsSsd);
public record AtaCounterMapping(string Id, string ModelPattern, string FirmwarePattern, byte Attribute, int RawBytes, decimal BytesPerUnit, byte? HoursAttribute, string Documentation)
{
    public bool Matches(string model, string firmware) => Regex.IsMatch(model, ModelPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) && Regex.IsMatch(firmware, FirmwarePattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
public static class AtaMappings
{
    public static IReadOnlyList<AtaCounterMapping> All { get; } = new[] {
        new AtaCounterMapping("mx500-f6-512-v1",@"^CT1000MX500SSD1$",@"^M3CR[0-9]{3}$",246,6,512,9,
            "Crucial MX500 verified interpretation; Micron Storage Executive SMART table: F6 cumulative host sectors, 512 bytes; ID 9 hours. https://assets.micron.com/adobe/assets/urn%3Aaaid%3Aaem%3A71922363-0144-4893-baae-5ecfa6f5e0b2/original/as/2400text.pdf"),
        new AtaCounterMapping("intel-320-e1-32mib-v1",@"^INTEL SSDSA2CW(?:040|080|120|160|300|600)G3[A-Z0-9]*$",@"^.*$",225,6,33554432,9,
            "Intel SSD 320 Product Specification 325152-002US, Table 12: E1 increments per 65,536 x 512-byte sectors; 09 power-on hours. No firmware-dependent unit variation documented. https://www.intel.de/content/dam/www/public/us/en/documents/product-specifications/ssd-320-specification.pdf")
    };
    public static AtaCounterMapping? Find(string model, string firmware) => All.SingleOrDefault(m => m.Matches(model.Trim(), firmware.Trim()));
    public static decimal? VerifiedTbw(DriveInfo drive) => drive.Model == "CT1000MX500SSD1" && drive.Capacity is >= 999_000_000_000 and <= 1_001_000_000_000 ? 360m : null;
    // Firmware-specific width validation, exercised with synthetic regression data.
    public static bool CheckStatisticsWidth(AtaIdentity identity) => identity.Model == "CT1000MX500SSD1" && identity.Firmware == "M3CR043" && identity.LogicalSectorBytes == 512;
}
public static class AtaDecode
{
    public static void Sector(byte[] b) { if (b.Length != 512) throw new IOException("ATA response must contain exactly 512 bytes"); }
    static ushort Word(byte[] b, int i) => BitConverter.ToUInt16(b, i * 2);
    public static AtaIdentity Identify(byte[] b)
    {
        Sector(b); if (Word(b, 0) is 0 or 0xFFFF || (Word(b, 0) & 0x8000) != 0) throw new IOException("Invalid ATA IDENTIFY DEVICE data");
        if (b[510] == 0xA5 && (b.Sum(v => (int)v) & 255) != 0) throw new IOException("IDENTIFY checksum mismatch");
        string Text(int start, int count) { var text = new byte[count * 2]; for (int i = 0; i < count; i++) { text[i * 2] = b[(start + i) * 2 + 1]; text[i * 2 + 1] = b[(start + i) * 2]; } return Encoding.ASCII.GetString(text).Trim(); }
        int size = 512; ushort w = Word(b, 106);
        if ((w & 0xC000) == 0x4000 && (w & 0x1000) != 0)
        {
            uint words = BitConverter.ToUInt32(b, 234);
            if (words <= 256 || words > 524288 || (words & (words - 1)) != 0) throw new IOException("Invalid IDENTIFY logical sector size");
            size = checked((int)words * 2);
        }
        string model = Text(27, 20), serial = Text(10, 10), firmware = Text(23, 4);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serial)) throw new IOException("IDENTIFY identity missing");
        ushort rotation = Word(b, 217); return new(model, serial, firmware, size, rotation == 1 ? true : rotation is >= 0x401 and <= 0xFFFE ? false : null);
    }
    public static (decimal? Bytes, decimal? Hours, string Detail) Statistics(byte[] b, int sectorBytes)
    {
        Sector(b);
        if (BitConverter.ToUInt16(b, 0) != 1 || b[2] != 1) throw new IOException("Unrecognized Device Statistics general-page header");
        decimal? Count(int offset, int valueBytes)
        {
            ulong raw = BitConverter.ToUInt64(b, offset);
            if ((raw & (3UL << 62)) != (3UL << 62) || (raw & (1UL << 61)) != 0) return null;
            return raw & (valueBytes == 6 ? 0xFFFFFFFFFFFFUL : 0xFFFFFFFFUL);
        }
        var written = Count(24, 6); var hours = Count(16, 4);
        return (written * sectorBytes, hours, written == null ? "Device Statistics Logical Sectors Written unsupported, invalid, or normalized" : "ATA Device Statistics 04h/01h, offset 18h · logical sectors × " + sectorBytes + " bytes");
    }
    public static Dictionary<byte, ulong> Smart(byte[] b)
    {
        Sector(b); if (BitConverter.ToUInt16(b, 0) is 0 or 0xFFFF) throw new IOException("Invalid SMART data revision");
        if ((b.Sum(v => (int)v) & 255) != 0) throw new IOException("SMART data checksum mismatch");
        var attributes = new Dictionary<byte, ulong>();
        for (int i = 0; i < 30; i++) { int offset = 2 + i * 12; byte id = b[offset]; if (id == 0) continue; ulong raw = 0; for (int j = 0; j < 6; j++) raw |= (ulong)b[offset + 5 + j] << (8 * j); if (!attributes.TryAdd(id, raw)) throw new IOException("Duplicate SMART attribute"); }
        return attributes;
    }
    public static DriveTelemetry MappedSmart(byte[] smart, AtaIdentity identity)
    {
        var attributes = Smart(smart); var map = AtaMappings.Find(identity.Model, identity.Firmware);
        if (map == null) return new(null, "", null, $"Unrecognized host-write counter mapping for {identity.Model}, firmware {identity.Firmware}; no generic attribute-ID guess is used");
        decimal? hours = map.HoursAttribute is byte id && attributes.TryGetValue(id, out var h) ? h : null;
        if (!attributes.TryGetValue(map.Attribute, out var raw)) return new(null, "", hours, $"Mapped host-write attribute {map.Attribute} is absent");
        return new(raw * map.BytesPerUnit, "ata-smart-" + map.Id, hours, $"SMART {map.Attribute} · unsigned little-endian raw{map.RawBytes * 8} × {map.BytesPerUnit:N0} bytes · {identity.Model} / {identity.Firmware}");
    }
}
public static class AtaTelemetry
{
    public static DriveTelemetry Read(IAtaTransport transport, DriveInfo drive)
    {
        var identity = AtaDecode.Identify(transport.Read(AtaTelemetryCommand.Identify));
        // Recheck the command's returned identity, not only the Windows descriptor.
        // Storage descriptors and IDENTIFY may differ in presentation; serial must still match exactly.
        if (WindowsStorage.StableId(identity.Model, identity.Serial) != drive.Id) throw new DriveIdentityException("ATA identity changed or disagrees with Windows descriptor; refresh drives");
        if (identity.IsSsd == false) throw new IOException("IDENTIFY reports rotating media; SSD telemetry unavailable");
        decimal? hours = null; var notes = new List<string>();
        try
        {
            var stats = AtaDecode.Statistics(transport.Read(AtaTelemetryCommand.DeviceStatistics), identity.LogicalSectorBytes); hours = stats.Hours;
            if (stats.Bytes != null)
            {
                if (AtaMappings.CheckStatisticsWidth(identity))
                {
                    var crossCheck = AtaDecode.MappedSmart(transport.Read(AtaTelemetryCommand.SmartReadData), identity);
                    if (crossCheck.HostBytes == null) return crossCheck with { PowerOnHours = hours ?? crossCheck.PowerOnHours, Detail = "Known M3CR043 statistics-width quirk: full-width SMART validation unavailable. " + crossCheck.Detail };
                    decimal modulus = 4294967296m * 512;
                    if (crossCheck.HostBytes >= modulus && stats.Bytes < modulus)
                    {
                        decimal low = crossCheck.HostBytes.Value % modulus;
                        bool confirmed = low == stats.Bytes;
                        if (!confirmed)
                        {
                            // Bracket the SMART read so concurrent writes need not match exactly.
                            var after = AtaDecode.Statistics(transport.Read(AtaTelemetryCommand.DeviceStatistics), 512);
                            if (after.Bytes is decimal end) confirmed = (low - stats.Bytes.Value + modulus) % modulus <= (end - stats.Bytes.Value + modulus) % modulus;
                        }
                        if (confirmed) return crossCheck with { PowerOnHours = hours ?? crossCheck.PowerOnHours, Detail = "MX500 M3CR043 Device Statistics counter is truncated to 32 bits (confirmed against F6); full-width mapped SMART used. " + crossCheck.Detail };
                        return new(null, "", hours, "Known M3CR043 statistics-width quirk: standard/SMART counters disagree and truncation could not be confirmed");
                    }
                }
                return new(stats.Bytes, $"ata-devstat-04-01-18-ls{identity.LogicalSectorBytes}-v1", hours, stats.Detail);
            }
            notes.Add(stats.Detail);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { notes.Add("Device Statistics: " + WindowsStorage.Error(ex)); }
        try
        {
            var mapped = AtaDecode.MappedSmart(transport.Read(AtaTelemetryCommand.SmartReadData), identity);
            return mapped with { PowerOnHours = hours ?? mapped.PowerOnHours, Detail = string.Join("; ", notes.Append(mapped.Detail)) };
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { notes.Add("SMART: " + WindowsStorage.Error(ex)); }
        throw new IOException(string.Join("; ", notes));
    }
}
