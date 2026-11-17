using System.IO;
using DiskHeartbeat;
static void Check(bool condition,string name) {if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name);}
if(args.Contains("--published-probe")) {PublishedProbe.Run(args[1]);return;}
if(args.Contains("--ui-sata-fixtures")) {SataUiChecks.Run();return;}
if(args.Contains("--ata-inspect")) {AtaInspect.Run(args[1],args[2]);return;}
if(args.Contains("--sata-copy-probe")) {await SataCopyProbe.Run(args[1],args[2],args[3]);return;}
if(args.Contains("--attribution-probe")) {
    var reader=new NativeDriveReader();
    using var tracker=new ProcessWriteTracker();
    tracker.SetDrives(reader.Enumerate()); tracker.Start(false);
    Console.WriteLine(tracker.Status);
    if(!tracker.Active) {Console.WriteLine("Live ETW not available with this token; no write probe performed."); return;}
    await Task.Delay(2000);
    string path=Path.Combine(Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR")!,"write-probe.bin");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using(var file=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough)) {
        var bytes=new byte[1024*1024]; Random.Shared.NextBytes(bytes);
        for(int i=0;i<4;i++) file.Write(bytes);
        file.Flush(true);
    }
    await Task.Delay(3000);
    tracker.Stop();
    var events=tracker.Drain();
    foreach(var write in events) Console.WriteLine($"{write.Process}: {write.Bytes:N0} bytes");
    Check(events.Any(e=>e.Bytes>0),"live physical disk-write events received");
    File.Delete(path);
    return;
}
if(args.Contains("--ui-smoke") || args.Contains("--ui-live")) {DashboardUiChecks.Run(args.Contains("--ui-live"),args.Contains("--capture"));return;}
if(args.Contains("--probe")) {
    var reader=new NativeDriveReader();
    try {var drives=reader.Enumerate(); Console.WriteLine($"Native SSDs found: {drives.Count}"); Console.WriteLine(reader.Diagnostics);
        foreach(var drive in drives) {try {var result=reader.Read(drive); Console.WriteLine($"{drive.Model} · disk {drive.DiskNumber} · {drive.Bus} · firmware {drive.Firmware}: {result.HostBytes:N0} host bytes; capacity {drive.Capacity}; source {result.Source}; powered on {result.PowerOnHours} h\n{result.Detail}");} catch(Exception ex) {Console.WriteLine("READ FAILED: "+ex.Message);}}
    } catch(Exception ex) {Console.WriteLine("PROBE FAILED: "+ex.Message);}
    return;
}
Check(Metrics.Age(.04m)=="3 years, 2 months","4% clock");
Check(Metrics.Age(1.1m)=="88 years, 0 months","clock beyond budget");
Check(12e9m/1200e12m*100==.001m,"session percent");
Check(Math.Abs(12e9m/1200e12m*80*365.2425m*24-7.012656m)<.00001m,"session hours");
var now=DateTimeOffset.Now;
var history=new List<Snapshot>{new(now.AddDays(-3),1e12m),new(now,4e12m)};
Check(Metrics.Today(history,now).Contains("not enough"),"multi-day gap not allocated to today");
Check(Metrics.Average(history).Contains("1,000.00") || Metrics.Average(history).Contains("1000.00"),"offline interval average");
history.Add(new(now.AddSeconds(1),1e9m));
Check(Metrics.Average(history).Contains("collecting"),"decrease resets rate");
Check(Metrics.ObservedWrites(new List<Snapshot>{new(now,100),new(now,150),new(now,10),new(now,35)})==75,"tracked activity excludes counter resets");
Check(Metrics.LatestFirstUse(now,48)==now.AddDays(-2),"power-on latest first-use bound");
Check(Metrics.LatestFirstUse(now,decimal.MaxValue)==null,"invalid hours rejected");
Check(Metrics.ActivityCost(12e9m,1200m).Contains("0.001% TBW"),"activity rated percentage");
Check(Metrics.ActivityCost(12e9m,null).Contains("TBW unknown"),"unknown activity budget");
var paceHistory=new List<Snapshot>{new(now.AddDays(-10),0),new(now,1e12m)};
Check(Metrics.RecordedPace(paceHistory)?.BytesPerDay==100e9m,"recorded calendar-day pace");
Check(Metrics.Projection(paceHistory,1200).Contains("~32.8 years") && Metrics.Projection(paceHistory,1200).Contains("10.0-day window"),"recorded forecast and window");
Check(Metrics.Projection(paceHistory,null).Contains("TBW unknown"),"forecast missing rating");
Check(Metrics.Projection(paceHistory,1)=="Rated write budget reached","forecast beyond rating");
Check(Metrics.Projection(new(){new(now.AddDays(-2),1),new(now,1)},2).Contains("no writes"),"zero pace has no finite forecast");
Check(Metrics.Projection(new(){new(now.AddDays(-2),100),new(now,1)},2).Contains("collecting"),"reset invalidates forecast window");
Check(Metrics.InferredLifetime(new(now,48e12m,12000),1200).Contains("96.00 GB/powered-on day"),"inferred pre-app lifetime rate");
Check(Metrics.InferredLifetime(new(now,1e12m,0),1200).Contains("unavailable"),"zero hours inference unavailable");
var accumulator=new WriteAccumulator();
accumulator.Add("stable-a","app",10); accumulator.Add("stable-b","app",20); accumulator.Add("stable-a","app",15);
var attributed=accumulator.Drain();
Check(attributed.Single(p=>p.DriveId=="stable-a").Bytes==25 && attributed.Single(p=>p.DriveId=="stable-b").Bytes==20,"attribution aggregates by stable drive and process");
Check(accumulator.Drain().Count==0,"drain does not double count");
AtaFixtureChecks.Run();
PersistenceChecks.Run();
Console.WriteLine("All calculation checks passed.");
