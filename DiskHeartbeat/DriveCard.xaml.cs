using System.Windows;
using System.Windows.Controls;
namespace DiskHeartbeat;
public partial class DriveCard : UserControl {
    public DriveDashboardState State {get;}
    public event Action<DriveCard>? SaveRequested;
    public event Action<DriveCard>? ReadRequested;
    public event Action<DriveCard>? StartRequested;
    public event Action<DriveCard>? StopRequested;
    public DriveCard(DriveDashboardState state){State=state;InitializeComponent();Rating.Text=state.History.Tbw?.ToString()??"";Render();}
    void SaveRating(object sender,RoutedEventArgs e)=>SaveRequested?.Invoke(this);
    void ReadNow(object sender,RoutedEventArgs e)=>ReadRequested?.Invoke(this);
    void StartSession(object sender,RoutedEventArgs e)=>StartRequested?.Invoke(this);
    void StopSession(object sender,RoutedEventArgs e)=>StopRequested?.Invoke(this);
    void ScopeChanged(object sender,SelectionChangedEventArgs e){if(IsInitialized)RenderSources();}
    public string RatingText=>Rating.Text;
    public void SetRatingText(string value)=>Rating.Text=value;
    public void RenderSources(){
        State.UpdateSessionSources();
        var totals=SourceScope.SelectedIndex switch{1=>State.History.ProcessWrites,2=>State.SessionSources,_=>State.RunProcesses};
        if(SourceScope.SelectedIndex==2&&State.SessionSourceBaseline==null){SourceRows.Text="Start a session to see its apps";return;}
        var rows=totals.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ToList();
        SourceRows.Text=rows.Count==0?"No observed writes recorded":string.Join("\n",rows.Take(8).Select(p=>$"{p.Key} · {Metrics.ActivityCost(p.Value,State.History.Tbw)}"));
        if(rows.Count>8)SourceRows.Text+=$"\nOther apps · {Metrics.ActivityCost(rows.Skip(8).Sum(p=>p.Value),State.History.Tbw)}";
    }
    public void Render(){
        var s=State;var d=s.Drive;var h=s.History;var p=s.LastGood;
        NameLabel.Text=DriveDashboardState.FriendlyName(d.Model);NameLabel.ToolTip=$"{d.Model}\nPhysical disk {d.DiskNumber} · Firmware {d.Firmware}";
        DriveType.Text=$"{(d.Capacity is long capacity?$"{capacity/1e12:0.##} TB":"Capacity unknown")} · {d.Bus}";
        bool ready=!s.Reading&&!s.Boundary;
        Start.IsEnabled=ready&&s.SessionStart==null;Stop.IsEnabled=ready&&s.SessionStart!=null;
        Session.Text=s.SessionText;
        Status.Text=s.Error!=null?"Reading unavailable · last good data retained":s.Notice;
        Status.ToolTip=s.Error??s.Telemetry?.Detail;
        if(s.HasLifetime&&p!=null){
            if(h.Tbw is decimal tbw){decimal fraction=p.Bytes/(tbw*1e12m);Clock.Text=Metrics.Age(fraction)+" old";Numbers.Text=$"{p.Bytes/1e12m:N3} TB written of {tbw:N0} TB rated · {fraction*100:N3}%";Budget.Value=(double)Math.Min(100m,fraction*100);Milestone.Text=fraction>=1?"Rated write budget reached":"";}
            else{Clock.Text="Add a rated budget";Numbers.Text=$"{p.Bytes/1e12m:N3} TB lifetime host writes · rating unknown";Budget.Value=0;Milestone.Text="";}
            string today=Metrics.Today(h.Readings,DateTimeOffset.Now);Today.ToolTip=today;
            Today.Text=today.Contains(" GB observed")?today[7..today.IndexOf(" observed")]+" observed":today.Contains("discontinuity")?"New baseline":"Collecting readings";
            var pace=Metrics.RecordedPace(h.Readings);Average.Text=pace is {} rate?$"{rate.BytesPerDay/1e9m:N2} GB/day":"Needs 24 h";Average.ToolTip=Metrics.Average(h.Readings);
            Projection.Text=Metrics.Projection(h.Readings,h.Tbw).Replace("Time to rated write limit at current pace: ","");
        }else{Clock.Text="Lifetime clock unavailable";Numbers.Text=p==null?"Lifetime host writes unavailable":$"Last good: {p.Bytes/1e12m:N3} TB host writes";Budget.Value=0;Milestone.Text="";Today.Text=Average.Text="Unavailable";Projection.Text="Lifetime counter unavailable";Today.ToolTip=Average.ToolTip="A recognized lifetime counter is needed; observed ETW writes remain separate.";}
        RunLabel.Text=s.HasLifetime?"THIS RUN":"THIS RUN · OBSERVED";RecordedLabel.Text=s.HasLifetime?"SINCE TRACKING BEGAN":"RECORDED · OBSERVED";
        RunWrites.Text=$"{(s.HasLifetime?s.AppWrites:s.ObservedRun)/1e9m:N2} GB";RecordedWrites.Text=$"{(s.HasLifetime?Metrics.ObservedWrites(h.Readings):h.ProcessWrites.Values.Sum())/1e9m:N2} GB";
        RunWrites.ToolTip=Metrics.ActivityCost(s.HasLifetime?s.AppWrites:s.ObservedRun,h.Tbw)+(s.HasLifetime?" · SMART counter intervals":" · observed ETW only");
        RecordedWrites.ToolTip=Metrics.ActivityCost(s.HasLifetime?Metrics.ObservedWrites(h.Readings):h.ProcessWrites.Values.Sum(),h.Tbw)+" · observed periods; counter/source discontinuities excluded";
        CounterSource.Text=$"{d.Model} · disk {d.DiskNumber} · firmware {d.Firmware}\nCounter source: {s.Telemetry?.Source??p?.Source??"awaiting reading"}\n{s.Telemetry?.Detail}"+(s.Error!=null?"\nLast poll: "+s.Error:"");
        LastGood.Text=p==null?"No successful lifetime reading yet":$"Last good reading: {p.At.LocalDateTime:F}";
        FirstTracked.Text=h.Readings.Count>0?$"Tracking began: {h.Readings[0].At.LocalDateTime:g}":"Tracking begins with a successful reading";
        if(s.Telemetry?.PowerOnHours is decimal hours){FirstTracked.Text+=$"\nPowered on: {hours:N0} h";var bound=Metrics.LatestFirstUse(s.TelemetryAt??DateTimeOffset.Now,hours);if(bound is {} at)FirstTracked.Text+=$"\nFirst use: on or before ~{at.LocalDateTime:yyyy-MM-dd}";}
        LifetimePace.Text=Metrics.InferredLifetime(h.LifetimeBaseline,h.Tbw,p?.Bytes);Interval.Text=s.Interval;
        Gap.Text="";for(int i=h.Readings.Count-1;i>0;i--){var a=h.Readings[i-1];var b=h.Readings[i];if(a.At.LocalDateTime.Date!=b.At.LocalDateTime.Date){Gap.Text=Metrics.Compatible(a,b)?$"Cross-day interval: +{(b.Bytes-a.Bytes)/1e9m:N3} GB · {a.At.LocalDateTime:g} → {b.At.LocalDateTime:g} · daily allocation unknown":"Cross-day interval: counter/source discontinuity";break;}}
        RenderSources();
    }
}
