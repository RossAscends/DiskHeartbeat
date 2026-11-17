using System.Diagnostics;
using System.IO;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
namespace DiskHeartbeat;

public record AttributedWrite(string DriveId, string Process, decimal Bytes);
// Only aggregates are retained. No file names, stacks, command lines or raw ETL files.
public sealed class WriteAccumulator
{
    readonly object gate = new();
    readonly Dictionary<(string Drive, string Process), decimal> pending = new();
    public void Add(string drive, string process, decimal bytes)
    {
        if (bytes <= 0) return;
        lock (gate)
        {
            // Bound memory when many distinct executables appear between polls.
            if (pending.Count >= 2000 && !pending.ContainsKey((drive, process))) process = "Other / unattributed";
            var key = (drive, process); pending[key] = pending.GetValueOrDefault(key) + bytes;
        }
    }
    public IReadOnlyList<AttributedWrite> Drain()
    {
        lock (gate) { var result = pending.Select(p => new AttributedWrite(p.Key.Drive, p.Key.Process, p.Value)).ToArray(); pending.Clear(); return result; }
    }
}
public sealed class ProcessWriteTracker : IDisposable
{
    readonly object gate = new();
    Dictionary<int, string> drives = new();
    readonly WriteAccumulator accumulator = new();
    TraceEventSession? session;
    Task? consumer;
    volatile bool active;
    volatile string status = "Off";
    public bool Active => active;
    public string Status
    {
        get
        {
            try { var lost = session?.EventsLost ?? 0; return status + (lost > 0 ? $" · {lost:N0} events lost (incomplete)" : ""); }
            catch { return status; }
        }
    }
    public void SetDrives(IEnumerable<DriveInfo> list)
    {
        var map = new Dictionary<int, string>();
        foreach (var d in list) if (d.DiskNumber is int disk) map[disk] = d.Id;
        lock (gate) drives = map;
    }
    public void InvalidateDrive(string id) { lock (gate) drives = drives.Where(p => p.Value != id).ToDictionary(p => p.Key, p => p.Value); }
    public void Start(bool synthetic = false)
    {
        Stop();
        if (synthetic) { active = true; status = "Fixture process activity"; return; }
        TraceEventSession? created = null;
        try
        {
            if (TraceEventSession.IsElevated() != true) { status = "Unavailable · restart as administrator to enable process tracing"; return; }
            // A unique Windows system-logger session; never take over another tool's session.
            created = new TraceEventSession($"DiskHeartbeat-{Environment.ProcessId}-{Guid.NewGuid():N}") { StopOnDispose = true, BufferSizeMB = 8 };
            var names = new Dictionary<int, string>();
            foreach (var process in Process.GetProcesses())
            {
                using (process) { try { names[process.Id] = process.ProcessName; } catch { } }
            }
            // Accessing Source starts a session. Kernel flags must be enabled first.
            created.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
            var source = created.Source;
            source.Kernel.ProcessStart += e => names[e.ProcessID] = Path.GetFileNameWithoutExtension(e.ImageFileName);
            source.Kernel.ProcessDCStart += e => names[e.ProcessID] = Path.GetFileNameWithoutExtension(e.ImageFileName);
            source.Kernel.ProcessStop += e => names.Remove(e.ProcessID);
            source.Kernel.DiskIOWrite += e =>
            {
                if (e.TransferSize <= 0) return;
                string? id; lock (gate) drives.TryGetValue(e.DiskNumber, out id);
                if (id == null) return;
                string name = e.ProcessID == 4 ? "System" : names.GetValueOrDefault(e.ProcessID) ?? "System / unattributed";
                accumulator.Add(id, string.IsNullOrWhiteSpace(name) ? "System / unattributed" : name, e.TransferSize);
            };
            session = created;
            active = true; status = "Recording · observed disk writes (ETW)";
            consumer = Task.Factory.StartNew(() =>
            {
                try { source.Process(); if (active) status = "Tracing ended; toggle off/on to retry"; }
                catch (Exception ex) { status = "Tracing stopped: " + ex.Message; }
                finally { active = false; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex) { created?.Dispose(); session = null; active = false; status = "Unavailable: " + ex.Message; }
    }
    public void AddFixtureWrite(string drive, decimal bytes) { if (active) accumulator.Add(drive, "Fixture writer", bytes); }
    public IReadOnlyList<AttributedWrite> Drain() => accumulator.Drain();
    public void Stop()
    {
        active = false;
        var current = session; session = null;
        try { current?.Dispose(); } catch (Exception ex) { status = "Could not stop trace: " + ex.Message; }
        // Dispose stops Process(); wait only briefly to avoid blocking the UI on a driver issue.
        try { consumer?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        consumer = null; status = "Off";
    }
    public void Dispose() => Stop();
}
