using System.IO;
using System.Text.Json;
using DiskHeartbeat;
public static class PersistenceChecks {
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);Console.WriteLine("PASS "+label);}
    public static void Run(){
        string directory=Path.Combine(Path.GetTempPath(),"DiskHeartbeat-storage-checks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string legacy=Path.Combine(directory,"legacy","history.json"),portable=Path.Combine(directory,"portable","Data","history.json");Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var reading=new Snapshot(DateTimeOffset.Parse("2000-01-01T12:00:00+00:00"),57600000000000m,"ata-smart-mx500-f6-512-v1");
        var original=new HistoryStore{AttributionEnabled=false,Drives=new(){["stable-drive"]=new(){Tbw=360,Readings=new(){reading},ProcessWrites=new(){["Writer"]=12e9m}}}};
        string originalJson=JsonSerializer.Serialize(original);File.WriteAllText(legacy,originalJson);
        var loaded=HistoryStore.LoadAt(portable,legacy);
        Check(File.Exists(portable)&&loaded.Drives["stable-drive"].Tbw==360&&loaded.Drives["stable-drive"].Readings[0]==reading,"first portable launch imports ratings and source-tagged history");
        Check(!loaded.AttributionEnabled&&loaded.Drives["stable-drive"].ProcessWrites["Writer"]==12e9m,"migration preserves tracking preference and process totals");
        Check(File.ReadAllText(legacy)==originalJson,"legacy file remains untouched");
        loaded.Drives["stable-drive"].Tbw=400;loaded.Save();
        Check(File.Exists(portable+".bak")&&HistoryStore.LoadAt(portable,legacy).Drives["stable-drive"].Tbw==400,"portable history wins and subsequent saves remain portable with backup");
        Check(File.ReadAllText(legacy)==originalJson,"portable saves do not write back to LocalAppData source");
        Check(HistoryStore.DefaultDataDirectory==Path.Combine(AppContext.BaseDirectory,"Data"),"default data path is Data beside app host");
        Check(HistoryStore.DataDirectoryForHost(@"C:\Portable\RenamedApp.exe",@"C:\Temp\bundle-cache")==@"C:\Portable\Data","self-extracting and renamed EXE uses actual executable folder");
        Check(HistoryStore.DataDirectoryForHost(@"C:\Sdk\dotnet.exe",@"C:\Source\bin")==@"C:\Source\bin\Data","dotnet-hosted DLL uses application folder rather than SDK folder");
        string? previous=Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR");
        try{Environment.SetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR",Path.Combine(directory,"override"));Check(HistoryStore.FilePath==Path.Combine(directory,"override","history.json"),"explicit data directory override retained");}
        finally{Environment.SetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR",previous);}
        File.WriteAllText(legacy,"invalid json");string rejected=Path.Combine(directory,"failed-import","history.json");
        bool rejectedImport=false;try{HistoryStore.LoadAt(rejected,legacy);}catch(JsonException){rejectedImport=true;}
        Check(rejectedImport&&!File.Exists(rejected)&&File.ReadAllText(legacy)=="invalid json","corrupt legacy data is preserved without creating a broken portable file");
        var blank=HistoryStore.LoadAt(Path.Combine(directory,"fresh","Data","history.json"));blank.Save();
        Check(File.Exists(Path.Combine(directory,"fresh","Data","history.json")),"fresh install creates its portable data subfolder");
        Console.WriteLine("Portable persistence checks passed.");
    }
}
