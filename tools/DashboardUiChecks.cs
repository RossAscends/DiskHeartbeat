using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskHeartbeat;
public static class DashboardUiChecks {
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);Console.WriteLine("PASS "+label);}
    public static void Run(bool live,bool capture){
        if(Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR")==null)Environment.SetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR",Path.Combine(Path.GetTempPath(),"DiskHeartbeat-checks-"+Guid.NewGuid().ToString("N")));
        Exception? failure=null;
        var thread=new Thread(()=>{
            var reader=live?null:new DashboardFixtureReader();var app=new Application();var window=new MainWindow(reader,!live);
            window.Loaded+=async(_,_)=>{
                try{
                    async Task Call(string method,params object[] args)=>await (Task)typeof(MainWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,args)!;
                    async Task Idle(){for(int i=0;i<100;i++){if(window.DriveCards.Count>0&&window.DriveCards.All(c=>!c.State.Reading)&&window.DriveCards.All(c=>c.State.Telemetry!=null||c.State.Error!=null))return;await Task.Delay(100);}throw new Exception("Dashboard did not finish initial readings");}
                    string Text(DriveCard card,string name)=>((TextBlock)card.FindName(name)).Text;
                    await Idle();
                    var cards=window.DriveCards;
                    Check(cards.Count==2,"all drives visible together");
                    Check(window.FindName("Drives")==null&&window.FindName("Demo")==null,"selector and demo mode removed");
                    Check(((TextBlock)window.FindName("Delta")).Parent is WrapPanel,"activity indicator next to title");
                    var sata=cards.Single(c=>c.State.Drive.Bus=="SATA");var nvme=cards.Single(c=>c.State.Drive.Bus=="NVMe");
                    Check(cards[0].TranslatePoint(new Point(),window).Y==cards[1].TranslatePoint(new Point(),window).Y&&cards[1].TranslatePoint(new Point(),window).X>cards[0].TranslatePoint(new Point(),window).X,"two cards fit beside one another at default size");
                    if(live){Check(nvme.State.LastGood!=null&&nvme.State.Error==null,"live NVMe still reads on all-drive dashboard");Check(sata.State.LastGood!=null||sata.State.Error?.Contains("Access denied")==true,"SATA read or explicit access denial visible");return;}
                    nvme.SetRatingText("1200");
                    typeof(MainWindow).GetMethod("SaveRating",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{nvme});
                    Check(Text(sata,"Numbers").Contains("360 TB")&&Text(sata,"Numbers").Contains("16.000%"),"SATA clock and verified rating retained");
                    Check(Text(nvme,"Clock")=="3 years, 2 months old","NVMe clock retained");
                    if(capture){
                        var element=(FrameworkElement)window.Content;element.UpdateLayout();
                        var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
                        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR")!);
                        using var output=File.Create(Path.Combine(Environment.GetEnvironmentVariable("DISKHEARTBEAT_DATA_DIR")!,"dashboard.png"));png.Save(output);
                    }
                    reader!.Add(sata.State.Drive.Id,26e9m);reader.Add(nvme.State.Drive.Id,1e9m);await Call("PollAll");
                    Check(sata.State.AppWrites==26e9m&&nvme.State.AppWrites==1e9m,"all drives record without selecting or interacting with a card");
                    var persisted=HistoryStore.Load();Check(persisted.Drives[sata.State.Drive.Id].Readings.Last().Bytes==reader.Current(sata.State.Drive.Id).HostBytes&&persisted.Drives[nvme.State.Drive.Id].Readings.Last().Bytes==reader.Current(nvme.State.Drive.Id).HostBytes,"all drive snapshots persisted");
                    var tracker=(ProcessWriteTracker)typeof(MainWindow).GetField("processTracker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
                    tracker.AddFixtureWrite(sata.State.Drive.Id,26e9m);tracker.AddFixtureWrite(nvme.State.Drive.Id,1e9m);await Call("PollAll");
                    Check(Text(sata,"SourceRows").Contains("26 GB")&&Text(nvme,"SourceRows").Contains("1 GB"),"process attribution routed to each card");
                    var tray=typeof(MainWindow).GetField("tray",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;string tooltip=(string)tray.GetType().GetProperty("Text")!.GetValue(tray)!;
                    Check(tooltip.Length<=127&&tooltip.Contains("MX500")&&tooltip.Contains("P5 Plus")&&tooltip.Contains("%"),"tray summarizes both drives and budget percentages");
                    await Call("StartSession",sata);await Call("StartSession",nvme);
                    reader.Add(sata.State.Drive.Id,12e9m);reader.Add(nvme.State.Drive.Id,3e9m);await Call("PollAll");await Call("StopSession",sata);
                    Check(Text(sata,"Session").Contains("12.000 GB")&&nvme.State.SessionStart!=null,"independent per-drive sessions");
                    await Call("StopSession",nvme);Check(Text(nvme,"Session").Contains("3.000 GB"),"each session uses its own drive boundaries");
                    var before=sata.State.LastGood;reader.Failures[sata.State.Drive.Id]=new Win32Exception(5);reader.Add(nvme.State.Drive.Id,1e9m);await Call("PollAll");
                    Check(sata.State.LastGood==before&&Text(sata,"CounterSource").Contains("Access denied")&&Text(sata,"Status").Contains("unavailable"),"failure retains last reading and timestamp with readable status");
                    Check(nvme.State.AppWrites==5e9m,"unreadable drive does not prevent other drive recording");
                    reader.Failures.TryRemove(sata.State.Drive.Id,out _);await Call("StartSession",sata);
                    reader.Set(sata.State.Drive.Id,reader.Current(sata.State.Drive.Id) with{Source="ata-devstat-04-01-18-ls512-v1",HostBytes=100e12m});await Call("PollAll");
                    Check(sata.State.SessionStart==null&&Text(sata,"Session").Contains("invalidated")&&sata.State.AppWrites==38e9m,"source change invalidates only matching drive baseline and session");
                    reader.Set(sata.State.Drive.Id,reader.Current(sata.State.Drive.Id) with{HostBytes=0});await Call("PollAll");Check(Text(sata,"Numbers").Contains("0.000 TB"),"valid SATA zero retained");
                    reader.Set(sata.State.Drive.Id,new(null,"",10000,"Unrecognized host-write counter mapping"));await Call("PollAll");
                    tracker.AddFixtureWrite(sata.State.Drive.Id,2e9m);await Call("PollAll");Check(Text(sata,"Clock")=="Lifetime clock unavailable"&&Text(sata,"RunLabel").Contains("OBSERVED")&&sata.State.ObservedRun==28e9m,"ETW observed activity stays available without lifetime telemetry");
                    await Call("StartSession",sata);tracker.AddFixtureWrite(sata.State.Drive.Id,1e9m);await Call("StopSession",sata);Check(Text(sata,"Session").Contains("observed ETW")&&Text(sata,"Session").Contains("1.000 GB"),"observed session retained");
                    ((CheckBox)window.FindName("Attribution")).IsChecked=false;((CheckBox)window.FindName("Attribution")).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    Check(!HistoryStore.Load().AttributionEnabled&&!tracker.Active,"global app tracking toggle persists");
                    decimal old=nvme.State.AppWrites;window.WindowState=WindowState.Minimized;await Task.Delay(100);Check(!window.IsVisible&&!window.ShowInTaskbar,"minimize hides dashboard to tray");
                    reader.Add(nvme.State.Drive.Id,2e9m);await Task.Delay(5500);Check(nvme.State.AppWrites==old+2e9m,"timer continues recording all drives in tray");
                    typeof(MainWindow).GetMethod("RestoreWindow",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);Check(window.IsVisible&&window.WindowState==WindowState.Normal,"tray restore");window.Hide();
                    Check(reader.MaxConcurrent.Values.All(n=>n==1),"no overlapping reads per drive and all queries off UI thread");
                    Console.WriteLine("All-drive dashboard UI checks passed.");
                }catch(Exception ex){failure=ex;}finally{window.Close();app.Shutdown();}
            };
            window.ShowInTaskbar=false;window.Show();window.Hide();app.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw failure;
    }
}
public sealed class DashboardFixtureReader : IDriveReader {
    readonly DiskHeartbeat.DriveInfo[] drives={new(WindowsStorage.StableId("CT1000MX500SSD1","fixture-sata"),@"\\.\PhysicalDrive7","CT1000MX500SSD1",1000000000000,"SATA","M3CR043"),new(WindowsStorage.StableId("CT2000P5PSSD8","fixture-nvme"),@"\\.\PhysicalDrive9","CT2000P5PSSD8",2000000000000,"NVMe","TEST0001")};
    readonly ConcurrentDictionary<string,DriveTelemetry> values=new();readonly ConcurrentDictionary<string,int> inProgress=new();
    public ConcurrentDictionary<string,Exception> Failures {get;}=new();public ConcurrentDictionary<string,int> MaxConcurrent {get;}=new();
    public DashboardFixtureReader(){values[drives[0].Id]=new(57600000000000m,"ata-smart-mx500-f6-512-v1",10000,"Synthetic firmware-aware SATA reading");values[drives[1].Id]=new(48e12m,NvmeReader.CounterSource,12000,"Synthetic NVMe reading");}
    public DriveTelemetry Current(string id)=>values[id];public void Set(string id,DriveTelemetry value)=>values[id]=value;
    public void Add(string id,decimal bytes)=>values.AddOrUpdate(id,_=>throw new Exception(),(_,old)=>old with{HostBytes=old.HostBytes+bytes});
    public IReadOnlyList<DiskHeartbeat.DriveInfo> Enumerate()=>drives;
    public DriveTelemetry Read(DiskHeartbeat.DriveInfo drive){int n=inProgress.AddOrUpdate(drive.Id,1,(_,count)=>count+1);MaxConcurrent.AddOrUpdate(drive.Id,n,(_,count)=>Math.Max(count,n));try{if(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA)throw new Exception("Query executed on UI thread");Thread.Sleep(30);if(Failures.TryGetValue(drive.Id,out var failure))throw failure;return values[drive.Id];}finally{inProgress.AddOrUpdate(drive.Id,0,(_,count)=>count-1);}}
}
