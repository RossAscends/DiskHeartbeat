using DiskHeartbeat;
using System.IO;
using System.Text.Json;
public static class AtaInspect {
    public static void Run(string outputDirectory,string model){
        Directory.CreateDirectory(outputDirectory);
        var drive=new NativeDriveReader().Enumerate().Single(d=>d.Model==model);
        using var handle=WindowsStorage.Open(drive.Path,true);WindowsStorage.Revalidate(handle,drive);
        var transport=new NativeAtaTransport(handle);
        var identify=AtaDecode.Identify(transport.Read(AtaTelemetryCommand.Identify));
        var stats=transport.Read(AtaTelemetryCommand.DeviceStatistics);var smart=transport.Read(AtaTelemetryCommand.SmartReadData);
        var standard=AtaDecode.Statistics(stats,identify.LogicalSectorBytes);var mapped=AtaDecode.MappedSmart(smart,identify);
        // Keep raw identify/serial out of committed artifacts; the two telemetry sectors have no serial number.
        File.WriteAllBytes(Path.Combine(outputDirectory,"statistics.bin"),stats);File.WriteAllBytes(Path.Combine(outputDirectory,"smart.bin"),smart);
        File.WriteAllText(Path.Combine(outputDirectory,"counter-comparison.json"),JsonSerializer.Serialize(new{drive.Model,drive.DiskNumber,identify.Firmware,identify.LogicalSectorBytes,StandardBytes=standard.Bytes,StandardHours=standard.Hours,StandardRawValue=BitConverter.ToUInt64(stats,24).ToString("X16"),Mapped=mapped,RawAttributes=AtaDecode.Smart(smart)},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(File.ReadAllText(Path.Combine(outputDirectory,"counter-comparison.json")));
    }
}
