namespace DiskHeartbeat;

// Per-drive dashboard state is independent of WPF and of the transport readers.
public sealed class DriveDashboardState {
    public DriveInfo Drive {get;set;}
    public DriveHistory History {get;}
    public DriveTelemetry? Telemetry {get;private set;}
    public DateTimeOffset? TelemetryAt {get;private set;}
    public Snapshot? LastGood=>History.Readings.LastOrDefault();
    public bool HasLifetime=>LastGood!=null&&(Telemetry==null||Telemetry.HostBytes!=null);
    public string? Error {get;set;}
    public string Notice {get;private set;}="";
    public string Interval {get;private set;}="";
    public decimal AppWrites {get;private set;}
    Snapshot? appPrevious;
    decimal? pulseBaseline;
    decimal pulseWhole;
    public Dictionary<string,decimal> RunProcesses {get;}=new();
    public decimal ObservedRun=>RunProcesses.Values.Sum();
    public bool Reading {get;set;}
    public bool Boundary {get;set;}
    public decimal? SessionStart {get;set;}
    public bool SessionObserved {get;set;}
    public Dictionary<string,decimal>? SessionSourceBaseline {get;set;}
    public Dictionary<string,decimal> SessionSources {get;set;}=new();
    public string SessionText {get;set;}="Ready";
    public DriveDashboardState(DriveInfo drive,DriveHistory history){Drive=drive;History=history;}
    public (Snapshot? Point,decimal PulseGb) Accept(DriveTelemetry result,DateTimeOffset at){
        Telemetry=result;TelemetryAt=at;Error=null;
        if(result.HostBytes is not decimal bytes){Notice="Lifetime writes unavailable";return(null,0);}
        var point=new Snapshot(at,bytes,result.Source);var previous=LastGood;
        bool reset=previous!=null&&!Metrics.Compatible(previous,point);
        Notice=reset?(previous!.Source!=point.Source?"Counter source changed · new baseline":"Counter decreased · new baseline"):"";
        if(reset){
            History.LifetimeBaseline=null;pulseBaseline=bytes;pulseWhole=0;
            if(SessionStart!=null&&!SessionObserved){SessionStart=null;SessionSourceBaseline=null;SessionSources=new();SessionText="Session invalidated: counter/source changed.";}
        }
        if(result.PowerOnHours is >0)History.LifetimeBaseline??=new(at,bytes,result.PowerOnHours.Value,result.Source);
        if(appPrevious is {} old&&Metrics.Compatible(old,point))AppWrites+=bytes-old.Bytes;
        appPrevious=point;
        pulseBaseline??=bytes;
        decimal whole=decimal.Floor((bytes-pulseBaseline.Value)/1e9m),pulse=0;
        if(whole>pulseWhole){pulse=whole-pulseWhole;pulseWhole=whole;}
        if(previous!=null)Interval=reset?"Latest interval: counter/source discontinuity":$"Latest interval: +{(bytes-previous.Bytes)/1e9m:N3} GB · {previous.At.LocalDateTime:g} → {at.LocalDateTime:g}"+(previous.At.LocalDateTime.Date!=at.LocalDateTime.Date?" · daily allocation unknown":"");
        var readings=History.Readings;
        if(readings.Count>=2){var a=readings[^2];var b=readings[^1];if(a.At.ToUnixTimeSeconds()/60==at.ToUnixTimeSeconds()/60&&b.At.ToUnixTimeSeconds()/60==at.ToUnixTimeSeconds()/60&&Metrics.Compatible(a,b)&&Metrics.Compatible(b,point))readings.RemoveAt(readings.Count-1);}
        readings.Add(point);return(point,pulse);
    }
    public void UpdateSessionSources(){if(SessionStart!=null&&SessionSourceBaseline!=null)SessionSources=RunProcesses.ToDictionary(p=>p.Key,p=>Math.Max(0,p.Value-SessionSourceBaseline.GetValueOrDefault(p.Key)));}
    public static string FriendlyName(string model)=>model switch{"CT1000MX500SSD1"=>"Crucial MX500","CT2000P5PSSD8"=>"Crucial P5 Plus",_=>model};
    public static string ShortName(string model)=>model switch{"CT1000MX500SSD1"=>"MX500","CT2000P5PSSD8"=>"P5 Plus",_=>model.Length>10?model[..10]:model};
}
