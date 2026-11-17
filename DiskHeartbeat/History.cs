using System.IO;
using System.Text.Json;
namespace DiskHeartbeat;
// Legacy snapshots predate source tagging and were written only by the original NVMe reader.
public record Snapshot(DateTimeOffset At, decimal Bytes, string Source = NvmeReader.CounterSource);
public record LifetimeBaseline(DateTimeOffset At, decimal Bytes, decimal PowerOnHours, string Source = NvmeReader.CounterSource);
public sealed class DriveHistory
{
    public decimal? Tbw { get; set; }
    public List<Snapshot> Readings { get; set; } = new();
    public Dictionary<string, decimal> ProcessWrites { get; set; } = new();
    public DateTimeOffset? AttributionStarted { get; set; }
    public LifetimeBaseline? LifetimeBaseline { get; set; }
}
public sealed class HistoryStore
{
    public bool AttributionEnabled { get; set; } = true;
    public Dictionary<string, DriveHistory> Drives { get; set; } = new();
    // IncludeAllContentForSelfExtract can put AppContext.BaseDirectory in the
    // bundle cache. Use the real process EXE; only a dotnet-hosted source/DLL run
    // needs the managed application directory instead of the SDK's directory.
    public static string DefaultDataDirectory => DataDirectoryForHost(Environment.ProcessPath, AppContext.BaseDirectory);
    public static string DataDirectoryForHost(string? processPath, string managedDirectory)
    {
        string directory = processPath != null && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(processPath)! : managedDirectory;
        return Path.Combine(directory, "Data");
    }
    public static string FilePath => Path.Combine(Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR") ?? DefaultDataDirectory, "history.json");
    public static string LegacyFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskHeartbeat", "history.json");
    string? storageFile;
    public static HistoryStore Load() => LoadAt(FilePath, Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR") == null ? LegacyFilePath : null);
    public static HistoryStore LoadAt(string destination, string? legacyFile = null)
    {
        if (File.Exists(destination)) return ReadAt(destination, destination);
        if (legacyFile != null && File.Exists(legacyFile))
        {
            var migrated = ReadAt(legacyFile, destination);
            // Parse before writing, leave the original untouched, and never replace
            // an existing portable history (including a concurrent first launch).
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, migrated);
                    stream.Flush(true);
                }
                try { File.Move(temporary, destination, false); }
                catch (IOException) when (File.Exists(destination)) { return ReadAt(destination, destination); }
                return migrated;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new() { storageFile = destination };
    }
    static HistoryStore ReadAt(string source, string destination)
    {
        var history = JsonSerializer.Deserialize<HistoryStore>(File.ReadAllText(source)) ?? throw new IOException("History is empty.");
        if (history.Drives == null || history.Drives.Values.Any(d => d == null || d.Readings == null || d.ProcessWrites == null))
            throw new IOException("History contains invalid drive records.");
        history.storageFile = destination;
        return history;
    }
    public void Save()
    {
        string destination = storageFile ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        if (File.Exists(destination)) File.Replace(temp, destination, destination + ".bak"); else File.Move(temp, destination);
    }
}
public static class Metrics
{
    public static bool Compatible(Snapshot previous, Snapshot current) => previous.Source == current.Source && current.Bytes >= previous.Bytes;
    public static (decimal BytesPerDay, double Days)? RecordedPace(List<Snapshot> r)
    {
        if (r.Count < 2) return null;
        int start = 0;
        for (int i = 1; i < r.Count; i++) if (!Compatible(r[i - 1], r[i])) start = i;
        var window = r.Skip(start).Where(p => p.At >= r[^1].At.AddDays(-30)).ToList();
        if (window.Count < 2) return null;
        double days = (window[^1].At - window[0].At).TotalDays;
        return days < 1 ? null : ((window[^1].Bytes - window[0].Bytes) / (decimal)days, days);
    }
    public static string Projection(List<Snapshot> r, decimal? tbw)
    {
        const string label = "Time to rated write limit at current pace: ";
        if (tbw is not > 0) return label + "TBW unknown";
        if (r.Count == 0) return label + "awaiting readings";
        decimal remaining = tbw.Value * 1e12m - r[^1].Bytes;
        if (remaining <= 0) return "Rated write budget reached";
        var pace = RecordedPace(r);
        if (pace == null) return label + "collecting 24 h of history";
        if (pace.Value.BytesPerDay <= 0) return label + $"unavailable (no writes in {pace.Value.Days:N1}-day window)";
        decimal years = remaining / pace.Value.BytesPerDay / 365.2425m;
        return label + $"~{years:N1} years · {pace.Value.Days:N1}-day window";
    }
    public static string InferredLifetime(LifetimeBaseline? baseline, decimal? tbw, decimal? latestBytes = null)
    {
        if (baseline is not { PowerOnHours: > 0 } b) return "Lifetime inferred average: power-on baseline unavailable";
        decimal perDay = b.Bytes * 24 / b.PowerOnHours;
        string result = $"Lifetime inferred average: {perDay / 1e9m:N2} GB/powered-on day · baseline {b.At.LocalDateTime:yyyy-MM-dd}";
        decimal current = latestBytes ?? b.Bytes;
        if (tbw is > 0 && perDay > 0 && current < tbw * 1e12m) result += $"\nTime to rated write limit at current pace: ~{(tbw.Value * 1e12m - current) / perDay / 365.2425m:N1} years · lifetime inferred pace (assumes 24 h/day powered on)";
        else if (tbw is > 0 && current >= tbw * 1e12m) result += "\nRated write budget reached";
        return result;
    }
    public static DateTimeOffset? LatestFirstUse(DateTimeOffset at, decimal powerOnHours)
    {
        if (powerOnHours < 0 || powerOnHours > (decimal)(at - DateTimeOffset.MinValue).TotalHours) return null;
        return at.AddHours(-(double)powerOnHours);
    }
    public static string ActivityCost(decimal bytes, decimal? tbw) =>
        $"{bytes / 1e9m:0.##} GB (" + (tbw is > 0 ? $"{bytes / (tbw.Value * 1e12m) * 100:0.######}% TBW)" : "TBW unknown)");
    public static decimal ObservedWrites(IReadOnlyList<Snapshot> readings)
    {
        decimal total = 0;
        for (int i = 1; i < readings.Count; i++) if (Compatible(readings[i - 1], readings[i])) total += readings[i].Bytes - readings[i - 1].Bytes;
        return total;
    }
    public static string Age(decimal fraction)
    {
        decimal months = decimal.Floor(fraction * 80 * 12);
        return $"{decimal.Floor(months / 12):N0} years, {months % 12:0} months";
    }
    public static string ClockDuration(decimal fraction) => $"{fraction * 80 * 365.2425m * 24:N1} hours on the 80-year clock";
    public static string Today(List<Snapshot> readings, DateTimeOffset now)
    {
        var today = readings.Where(r => r.At.LocalDateTime.Date == now.LocalDateTime.Date).ToList();
        if (today.Count < 2) return "Today: not enough readings";
        // No midnight interpolation: show only the observed part of this day.
        for (int i = 1; i < today.Count; i++) if (!Compatible(today[i - 1], today[i])) return "Today: unavailable · counter/source discontinuity";
        return $"Today: {(today[^1].Bytes - today[0].Bytes) / 1e9m:N2} GB observed since {today[0].At.LocalDateTime:t} (partial day)";
    }
    public static string Average(List<Snapshot> r)
    {
        if (r.Count < 2) return "Average: collecting history (at least 24 hours)";
        int start = 0;
        for (int i = 1; i < r.Count; i++) if (!Compatible(r[i - 1], r[i])) start = i;
        var window = r.Skip(start).Where(p => p.At >= r[^1].At.AddDays(-30)).ToList();
        if (window.Count < 2 || (window[^1].At - window[0].At).TotalDays < 1) return "Average: collecting history (at least 24 hours)";
        double days = (window[^1].At - window[0].At).TotalDays;
        return $"Average: {(window[^1].Bytes - window[0].Bytes) / 1e9m / (decimal)days:N2} GB/day · {days:N1}-day window (up to 30 days)";
    }
}
