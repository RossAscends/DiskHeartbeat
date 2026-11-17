using System.IO;
using System.Text.Json;
using DiskHeartbeat;
public static class SataCopyProbe {
    // Uses the production reader. Does not read or write the app's history.
    public static async Task Run(string destinationRoot,string resultFile,string model){
        string volumeRoot=Path.GetPathRoot(Path.GetFullPath(destinationRoot))??throw new IOException("Missing drive root");
        if(volumeRoot.Length!=3||volumeRoot[1]!=':')throw new IOException("Use a drive-letter path on a simple SATA volume");
        var reader=new NativeDriveReader();var drives=reader.Enumerate();
        var drive=drives.Single(d=>d.Bus=="SATA"&&d.Model==model);
        using(var volume=WindowsStorage.Open(@"\\.\"+volumeRoot[..2])){
            var extents=new byte[256];int count=WindowsStorage.Io(volume,0x560000,Array.Empty<byte>(),extents);
            if(count<32||WindowsStorage.U(extents,0)!=1||WindowsStorage.U(extents,8)!=drive.DiskNumber)throw new IOException("Destination volume does not map exclusively to the selected SATA SSD physical disk");
        }
        // Read access / valid telemetry must work before any workload is created.
        var before=reader.Read(drive);var beforeAt=DateTimeOffset.Now;
        if(before.HostBytes==null)throw new IOException("No usable SATA host-write counter: "+before.Detail);
        using var tracker=new ProcessWriteTracker();tracker.SetDrives(drives);tracker.Start(false);
        string tracingStatus=tracker.Status;
        string nonce=Guid.NewGuid().ToString("N");
        string source=Path.Combine(Path.GetDirectoryName(resultFile)!,"copy-source-"+nonce+".bin");
        string directory=Path.Combine(Path.GetFullPath(destinationRoot),"DiskHeartbeat-verify-"+nonce);
        string destination=Path.Combine(directory,"copy.bin");
        const int fileBytes=64*1024*1024;
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        DriveTelemetry? after=null;DateTimeOffset afterAt=default;
        try{
            // Deterministic non-sparse data; source is outside the selected SATA volume.
            if(Path.GetPathRoot(source)==volumeRoot)throw new IOException("Test source must be on a different volume");
            using(var file=new FileStream(source,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                var block=new byte[1024*1024];Random.Shared.NextBytes(block);
                for(int i=0;i<64;i++)file.Write(block);file.Flush(true);
            }
            Directory.CreateDirectory(directory);
            File.Copy(source,destination,false);
            using(var file=new FileStream(destination,FileMode.Open,FileAccess.Write,FileShare.Read))file.Flush(true);
            for(int i=0;i<4;i++){
                await Task.Delay(3000);after=reader.Read(drive);afterAt=DateTimeOffset.Now;
                if(after.Source!=before.Source)throw new IOException("Source changed during copy test");
                if(after.HostBytes>before.HostBytes)break;
            }
            if(after?.HostBytes==null||after.HostBytes<before.HostBytes)throw new IOException("Missing/decreasing post-copy counter");
            decimal delta=after.HostBytes.Value-before.HostBytes.Value;
            tracker.Stop();var observed=tracker.Drain();
            var processWrites=observed.Select(w=>new{Disk=drives.Single(d=>d.Id==w.DriveId).DiskNumber,w.Process,w.Bytes}).ToArray();
            var record=new {drive.Model,drive.Bus,drive.DiskNumber,drive.Capacity,WindowsDescriptorFirmware=drive.Firmware,DestinationVolume=volumeRoot,FileBytes=fileBytes,BeforeAt=beforeAt,Before=before,AfterAt=afterAt,After=after,DeltaBytes=delta,CopyRatio=delta/fileBytes,Monotonic=true,Plausible=delta>=fileBytes&&delta<=fileBytes*8m,TraceStatus=tracingStatus,ObservedProcessWrites=processWrites,SataEtwObserved=observed.Any(w=>w.DriveId==drive.Id&&w.Bytes>0),NvmeEtwObserved=observed.Any(w=>drives.Single(d=>d.Id==w.DriveId).Bus=="NVMe"&&w.Bytes>0),Note="SMART interval includes other PC writes, buffering, filesystem overhead and test directory creation. Cleanup occurs after the second reading."};
            File.WriteAllText(resultFile,JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(File.ReadAllText(resultFile));
            if(delta==0)throw new IOException("Counter did not advance after the copy; test is inconclusive");
        }finally{
            if(File.Exists(destination))File.Delete(destination);
            if(Directory.Exists(directory))Directory.Delete(directory,false);
            if(File.Exists(source))File.Delete(source);
        }
    }
}
