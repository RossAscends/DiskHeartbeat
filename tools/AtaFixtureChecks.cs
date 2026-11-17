using System.ComponentModel;
using System.IO;
using System.Text.Json;
using DiskHeartbeat;
public static class AtaFixtureChecks {
    static void Check(bool test,string label){if(!test)throw new Exception(label);Console.WriteLine("PASS "+label);}
    static byte[] Fixture(string name)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures",name+".bin"));
    static void Reject(Action action,string label){try{action();}catch(IOException){Console.WriteLine("PASS "+label);return;}throw new Exception(label);}
    public static void Run(){
        var identity=AtaDecode.Identify(Fixture("identify-mx500"));
        Check(identity.Model=="CT1000MX500SSD1"&&identity.Serial=="fixture-serial-001"&&identity.LogicalSectorBytes==512&&identity.IsSsd==true,"IDENTIFY word-swapped identity and sector size");
        Check(AtaDecode.Identify(Fixture("identify-4kn")).LogicalSectorBytes==4096,"IDENTIFY 4Kn sector size");
        decimal expected=57600000000000m;
        var mapped=AtaDecode.MappedSmart(Fixture("smart-mx500"),identity);
        Check(mapped.HostBytes==expected&&mapped.PowerOnHours==10000,"MX500 F6 fixture: 112500000000 x 512 = 57600000000000");
        Check(Math.Abs(expected/360e12m*100-16m)<.000001m&&Metrics.Age(expected/360e12m)=="12 years, 9 months","MX500 360 TBW budget and 80-year clock (12.8 years)");
        var stats=AtaDecode.Statistics(Fixture("statistics-valid"),512);
        Check(stats.Bytes==expected&&stats.Hours==10000,"standard Device Statistics supported valid counters");
        Check(AtaDecode.Statistics(Fixture("statistics-valid"),4096).Bytes==expected*8,"standard counter uses logical sector size, not hard-coded 512");
        Check(AtaDecode.MappedSmart(Fixture("smart-zero"),identity).HostBytes==0&&AtaDecode.Statistics(Fixture("statistics-zero"),512).Bytes==0,"valid SMART and standard zero are usable");
        foreach(string type in new[]{"invalid","unsupported","normalized"})Check(AtaDecode.Statistics(Fixture("statistics-"+type),512).Bytes==null,type+" standard count rejected");
        var intel=AtaDecode.Identify(Fixture("identify-intel320"));
        Check(AtaDecode.MappedSmart(Fixture("smart-intel320"),intel).HostBytes==335544320,"verified Intel 320 E1 mapping uses 32 MiB, not sectors");
        Check(AtaDecode.MappedSmart(Fixture("smart-mx500"),AtaDecode.Identify(Fixture("identify-unknown"))).HostBytes==null,"attribute ID alone cannot identify units");
        Check(AtaDecode.MappedSmart(Fixture("smart-mx500"),identity with{Firmware="unverified"}).HostBytes==null,"firmware mapping constraint enforced");
        Check(AtaDecode.MappedSmart(Fixture("smart-nand-only"),identity).HostBytes==null,"NAND counter is not host writes");
        Reject(()=>AtaDecode.Smart(Fixture("smart-bad-checksum")),"SMART checksum rejected");
        Reject(()=>AtaDecode.Smart(Fixture("smart-truncated")),"SMART short response rejected");
        Reject(()=>AtaDecode.Statistics(Fixture("statistics-truncated"),512),"statistics short response rejected");
        var drive=new DiskHeartbeat.DriveInfo(WindowsStorage.StableId(identity.Model,identity.Serial),@"\\.\PhysicalDrive7",identity.Model,1000000000000,"SATA",identity.Firmware,true);
        var transport=new FixtureAtaTransport(Fixture("identify-mx500"),Fixture("statistics-valid"),Fixture("smart-mx500"));
        var preferred=AtaTelemetry.Read(transport,drive);
        Check(preferred.Source.StartsWith("ata-devstat")&&!transport.Calls.Contains(AtaTelemetryCommand.SmartReadData),"Device Statistics preferred over mapped SMART");
        transport=new(Fixture("identify-mx500"),Fixture("statistics-invalid"),Fixture("smart-mx500"));
        Check(AtaTelemetry.Read(transport,drive).Source==mapped.Source,"invalid standard counter falls back to MX500 mapping");
        transport=new(Fixture("identify-mx500"),null,Fixture("smart-mx500"));
        Check(AtaTelemetry.Read(transport,drive).HostBytes==expected,"controller unsupported standard path falls back");
        var quirkIdentity=AtaDecode.Identify(Fixture("identify-mx500-quirk"));
        var quirkDrive=drive with{Firmware=quirkIdentity.Firmware};
        transport=new(Fixture("identify-mx500-quirk"),Fixture("statistics-wrapped"),Fixture("smart-mx500"));
        var quirkReading=AtaTelemetry.Read(transport,quirkDrive);
        Check(quirkReading.HostBytes==expected&&quirkReading.Source==mapped.Source&&quirkReading.Detail.Contains("truncated to 32 bits"),"M3CR043 verified lower-32-bit statistics quirk uses full-width F6");
        transport=new(Fixture("identify-mx500-quirk"),Fixture("statistics-zero"),Fixture("smart-zero"));
        Check(AtaTelemetry.Read(transport,quirkDrive).Source.StartsWith("ata-devstat"),"standard remains preferred when full-width validation agrees");
        Reject(()=>AtaTelemetry.Read(transport,drive with{Id="changed"}),"ATA identity mismatch rejected");
        Check(AtaMappings.VerifiedTbw(drive)==360&&AtaMappings.VerifiedTbw(drive with{Capacity=2_000_000_000_000})==null,"rating requires verified model and capacity");
        if(IntPtr.Size==8){
            Check(AtaPassThrough.Response(Fixture("pass-through-x64"),560).SequenceEqual(Fixture("smart-mx500")),"full native ATA response validates and extracts payload");
            Reject(()=>AtaPassThrough.Response(Fixture("pass-through-error-x64"),560),"ATA ERR/ABRT status rejected");
            Reject(()=>AtaPassThrough.Response(Fixture("pass-through-truncated-x64"),559),"native short transfer rejected");
            var malformed=Fixture("pass-through-x64");WindowsStorage.Put(malformed,8,511);Reject(()=>AtaPassThrough.Response(malformed,560),"reported ATA underrun rejected");
        }
        var request=AtaPassThrough.Request(AtaTelemetryCommand.DeviceStatistics);
        Check(request[AtaPassThrough.TaskOffset+6]==0x2F&&request[AtaPassThrough.TaskOffset+2]==4&&request[AtaPassThrough.TaskOffset+3]==1&&(request[2]&0x10)!=0,"48-bit READ LOG EXT task file");
        request=AtaPassThrough.Request(AtaTelemetryCommand.SmartReadData);
        Check(request[AtaPassThrough.TaskOffset+6]==0xB0&&request[AtaPassThrough.TaskOffset]==0xD0&&request[AtaPassThrough.TaskOffset+3]==0x4F&&request[AtaPassThrough.TaskOffset+4]==0xC2,"read-only SMART READ DATA task file");
        var now=DateTimeOffset.Now;
        var a=new Snapshot(now.AddDays(-2),100,"source-a");var b=new Snapshot(now,1000,"source-b");
        Check(!Metrics.Compatible(a,b)&&Metrics.ObservedWrites(new List<Snapshot>{a,b})==0&&Metrics.RecordedPace(new(){a,b})==null,"source change resets totals and pace without subtracting");
        Check(!Metrics.Compatible(a,a with{At=now,Bytes=0}),"counter decrease resets baseline");
        var legacy=JsonSerializer.Deserialize<Snapshot>("{\"At\":\"2026-01-01T00:00:00+00:00\",\"Bytes\":100}")!;
        Check(legacy.Source==NvmeReader.CounterSource,"legacy NVMe history retains compatible source");
        Check(WindowsStorage.StableId(identity.Model,identity.Serial)==(drive with{Path=@"\\.\PhysicalDrive0"}).Id&&drive.DiskNumber==7,"stable history ID separate from ETW routing");
        Check(WindowsStorage.Error(new Win32Exception(5)).Contains("Access denied")&&WindowsStorage.Error(new Win32Exception(50)).Contains("Controller/driver"),"access and controller failures distinguished");
        Console.WriteLine("ATA fixture checks passed.");
    }
}
public sealed class FixtureAtaTransport : IAtaTransport {
    readonly byte[] identify;readonly byte[]? statistics;readonly byte[] smart;
    public List<AtaTelemetryCommand> Calls {get;}=new();
    public FixtureAtaTransport(byte[] identify,byte[]? statistics,byte[] smart){this.identify=identify;this.statistics=statistics;this.smart=smart;}
    public byte[] Read(AtaTelemetryCommand command){Calls.Add(command);return command switch{AtaTelemetryCommand.Identify=>identify,AtaTelemetryCommand.DeviceStatistics=>statistics??throw new Win32Exception(50),AtaTelemetryCommand.SmartReadData=>smart,_=>throw new ArgumentOutOfRangeException()};}
}
