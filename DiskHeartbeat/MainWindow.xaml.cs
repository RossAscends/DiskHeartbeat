using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace DiskHeartbeat;
public partial class MainWindow : Window {
    HistoryStore store=new();
    readonly NativeDriveReader native=new();
    readonly IDriveReader reader;
    readonly bool syntheticAttribution;
    readonly ProcessWriteTracker processTracker=new();
    readonly Dictionary<string,DriveCard> allCards=new();
    readonly Dictionary<string,decimal> observedPulses=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(5)};
    bool loading=true,discovering,polling,closing,storageWritable=true;
    DateTimeOffset pulseAt;
    TrayIcon? tray;
    public IReadOnlyList<DriveCard> DriveCards=>Cards.Items.Cast<DriveCard>().ToArray();
    public MainWindow():this(null){}
    // Reader injection is for SDK-driven fixture tests; there is no demo mode in the app.
    public MainWindow(IDriveReader? driveReader,bool simulateProcessActivity=false){
        reader=driveReader??native;syntheticAttribution=simulateProcessActivity;
        InitializeComponent();
        try{store=HistoryStore.Load();}catch(Exception ex){storageWritable=false;StorageStatus.Text="History could not be loaded; recording disabled: "+ex.Message;}
        Attribution.IsChecked=store.AttributionEnabled;loading=false;
        tray=new TrayIcon(RestoreWindow,()=>Close(),RefreshTray);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized){ShowInTaskbar=false;Hide();}};
        Closed+=(_,_)=>{closing=true;timer.Stop();processTracker.Dispose();CollectAttribution();Save();tray?.Dispose();tray=null;};
        Loaded+=async(_,_)=>await Discover();
        timer.Tick+=async(_,_)=>{if(DateTimeOffset.Now-pulseAt>TimeSpan.FromSeconds(10))Delta.Text="Listening for writes…";await PollAll();};timer.Start();
    }
    void Save(){if(!storageWritable)return;try{store.Save();StorageStatus.Text="";}catch(Exception ex){StorageStatus.Text="Persistence failed: "+ex.Message;}}
    void UpdateControls()=>Refresh.IsEnabled=!discovering&&!polling&&!DriveCards.Any(c=>c.State.Reading||c.State.Boundary||c.State.SessionStart!=null);
    async Task Discover(){
        if(discovering||polling||DriveCards.Any(c=>c.State.Reading||c.State.SessionStart!=null))return;
        discovering=true;UpdateControls();
        try{
            var drives=await Task.Run(()=>reader.Enumerate());if(closing)return;
            processTracker.Stop();CollectAttribution();processTracker.SetDrives(drives);
            if(store.AttributionEnabled)processTracker.Start(syntheticAttribution);
            Cards.Items.Clear();
            foreach(var drive in drives){
                if(!store.Drives.TryGetValue(drive.Id,out var history))store.Drives[drive.Id]=history=new(){Tbw=AtaMappings.VerifiedTbw(drive)};
                if(!allCards.TryGetValue(drive.Id,out var card)){
                    card=new(new(drive,history));allCards[drive.Id]=card;
                    card.SaveRequested+=SaveRating;card.ReadRequested+=async c=>await ReadCard(c);
                    card.StartRequested+=async c=>await StartSession(c);card.StopRequested+=async c=>await StopSession(c);
                }else card.State.Drive=drive;
                Cards.Items.Add(card);card.Render();
            }
            EmptyState.Visibility=drives.Count==0?Visibility.Visible:Visibility.Collapsed;
            EmptyState.Text="No native NVMe or SATA SSDs found. Expand discovery details below.";
            DiscoverySummary.Text=(ReferenceEquals(reader,native)?native.Diagnostics:"Fixture reader · no device access")+"\nMetrics: "+HistoryStore.FilePath;
            Status.Text="";RenderAttribution();Save();
        }catch(Exception ex){Status.Text="Discovery failed: "+WindowsStorage.Error(ex);}
        finally{discovering=false;UpdateControls();}
        await PollAll();
    }
    async void RefreshDrives(object sender,RoutedEventArgs e)=>await Discover();
    async Task PollAll(){
        if(closing||discovering||polling)return;polling=true;UpdateControls();
        try{await Task.WhenAll(DriveCards.Select(c=>ReadCard(c)));CollectAttribution();Save();RenderAll();}
        finally{polling=false;UpdateControls();}
    }
    async Task<Snapshot?> ReadCard(DriveCard card){
        var s=card.State;if(s.Reading||closing)return null;
        s.Reading=true;card.Render();UpdateControls();
        try{
            var result=await Task.Run(()=>reader.Read(s.Drive));if(closing)return null;
            var accepted=s.Accept(result,DateTimeOffset.Now);
            if(accepted.PulseGb>0)PulseActivity(s.Drive,accepted.PulseGb,false);
            CollectAttribution();UpdateSession(s);Save();return accepted.Point;
        }catch(Exception ex){
            if(ex is DriveIdentityException)processTracker.InvalidateDrive(s.Drive.Id);
            s.Error=WindowsStorage.Error(ex);return null;
        }finally{
            s.Reading=false;
            if(!closing){CollectAttribution();card.Render();RenderAttribution();RefreshTray();UpdateControls();}
        }
    }
    void PulseActivity(DriveInfo drive,decimal gb,bool observed){
        pulseAt=DateTimeOffset.Now;Delta.Text=$"{DriveDashboardState.ShortName(drive.Model)} +{gb:N0} GB"+(observed?" observed":"");
        Pulse.BeginAnimation(OpacityProperty,new DoubleAnimation(1,0.25,TimeSpan.FromMilliseconds(800)));
    }
    static void AddProcess(Dictionary<string,decimal> totals,string name,decimal bytes){if(totals.Count>=1000&&!totals.ContainsKey(name))name="Other / unattributed";totals[name]=totals.GetValueOrDefault(name)+bytes;}
    void CollectAttribution(){
        foreach(var w in processTracker.Drain()){
            if(!store.Drives.TryGetValue(w.DriveId,out var h))store.Drives[w.DriveId]=h=new();
            h.AttributionStarted??=DateTimeOffset.Now;AddProcess(h.ProcessWrites,w.Process,w.Bytes);
            if(allCards.TryGetValue(w.DriveId,out var card))AddProcess(card.State.RunProcesses,w.Process,w.Bytes);
        }
        foreach(var card in DriveCards){
            var s=card.State;s.UpdateSessionSources();
            if(!s.HasLifetime){decimal whole=decimal.Floor(s.ObservedRun/1e9m),previous=observedPulses.GetValueOrDefault(s.Drive.Id);if(whole>previous){observedPulses[s.Drive.Id]=whole;if(!closing)PulseActivity(s.Drive,whole-previous,true);}}
        }
    }
    void AttributionChanged(object sender,RoutedEventArgs e){
        if(loading)return;processTracker.Stop();CollectAttribution();store.AttributionEnabled=Attribution.IsChecked==true;
        if(store.AttributionEnabled)processTracker.Start(syntheticAttribution);
        Save();RenderAll();
    }
    void RenderAttribution(){
        string status=processTracker.Status;
        AttributionStatus.Text=!store.AttributionEnabled?"":processTracker.Active?"Recording all drives":status.Contains("administrator")?"Requires administrator":"App tracking unavailable";
        AttributionStatus.ToolTip=status+". ETW records only observed periods; its totals may differ from lifetime counters.";
    }
    void RenderAll(){foreach(var card in DriveCards)card.Render();RenderAttribution();RefreshTray();}
    void SaveRating(DriveCard card){
        var text=card.RatingText;
        if(string.IsNullOrWhiteSpace(text))card.State.History.Tbw=null;
        else if(decimal.TryParse(text,NumberStyles.Number,CultureInfo.CurrentCulture,out var value)&&value>0&&value<=1e9m)card.State.History.Tbw=value;
        else{card.State.Error="Enter a positive rated TBW up to 1,000,000,000, or leave blank for unknown.";card.Render();return;}
        Save();card.Render();RefreshTray();
    }
    async Task StartSession(DriveCard card){
        var s=card.State;if(s.Reading||s.Boundary||s.SessionStart!=null)return;s.Boundary=true;card.Render();UpdateControls();
        try{
            var point=await ReadCard(card);CollectAttribution();
            if(point==null&&(!processTracker.Active||s.Error!=null)){s.SessionText="Could not start: fresh reading or observed tracking unavailable.";return;}
            s.SessionObserved=point==null;s.SessionStart=s.SessionObserved?s.ObservedRun:point!.Bytes;
            s.SessionSourceBaseline=new(s.RunProcesses);s.SessionSources=new();
            s.SessionText=s.SessionObserved?"Running · observed writes":"Running · 0 GB";
        }finally{s.Boundary=false;card.Render();UpdateControls();}
    }
    async Task StopSession(DriveCard card){
        var s=card.State;if(s.Reading||s.Boundary||s.SessionStart is not decimal begin)return;s.Boundary=true;card.Render();UpdateControls();
        try{
            var point=await ReadCard(card);CollectAttribution();s.UpdateSessionSources();
            if(s.SessionStart==null)return;
            if(!s.SessionObserved&&point==null){s.SessionText="Stop reading failed · session remains active";return;}
            s.SessionText=SessionSummary((s.SessionObserved?s.ObservedRun:point!.Bytes)-begin,s.History.Tbw,s.SessionObserved?"Complete · observed ETW (recorded periods only)":"Complete");s.SessionStart=null;
        }finally{s.Boundary=false;card.Render();UpdateControls();}
    }
    static void UpdateSession(DriveDashboardState s){if(s.SessionStart is decimal begin){decimal? current=s.SessionObserved?s.ObservedRun:s.Telemetry?.HostBytes;if(current is decimal value)s.SessionText=SessionSummary(value-begin,s.History.Tbw,s.SessionObserved?"Running · observed ETW":"Running");}}
    static string SessionSummary(decimal bytes,decimal? tbw,string label)=>$"{label}: {bytes/1e9m:N3} GB\n"+(tbw is decimal t?$"{bytes/(t*1e12m)*100:N6}% of rated TBW · {Metrics.ClockDuration(bytes/(t*1e12m))}":"Rating unknown · clock cost unavailable");
    void RestoreWindow(){WindowState=WindowState.Normal;ShowInTaskbar=true;Show();Activate();}
    void RefreshTray(){
        if(tray==null)return;var cards=DriveCards;if(cards.Count==0){tray.Update("Disk Heartbeat\nNo drives detected");return;}
        string Amount(decimal bytes,decimal? tbw,int precision)=>$"{bytes/1e9m:0.##}GB"+(tbw is decimal t?$" ({(bytes/(t*1e12m)*100).ToString("0."+new string('#',precision),CultureInfo.CurrentCulture)}%)":" (TBW unknown)");
        string Compose(int precision,int count)=>"Run / recorded · % TBW\n"+string.Join("\n",cards.Take(count).Select(c=>{
            var s=c.State;decimal run=s.HasLifetime?s.AppWrites:s.ObservedRun;decimal total=s.HasLifetime?Metrics.ObservedWrites(s.History.Readings):s.History.ProcessWrites.Values.Sum();
            return DriveDashboardState.ShortName(s.Drive.Model)+(s.HasLifetime?": ":" [ETW]: ")+Amount(run,s.History.Tbw,precision)+" / "+Amount(total,s.History.Tbw,precision);
        }))+(cards.Count>count?$"\n+{cards.Count-count} more · open dashboard":"");
        string text=Compose(6,cards.Count);if(text.Length>127)text=Compose(4,cards.Count);
        for(int count=cards.Count-1;text.Length>127&&count>=0;count--)text=Compose(4,count);
        tray.Update(text);
    }
}
