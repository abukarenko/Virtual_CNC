using System.Globalization;
using VirtualCnc.Core;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int passed=0, failed=0;
void Test(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"expected {expected}; got {actual}"); }
void Near(double expected, double actual, double tolerance=0.005) { if (Math.Abs(expected-actual)>tolerance) throw new Exception($"expected {expected}; got {actual}"); }
Rig New(bool mist=false) => new(null,mist);
void Ok(Rig rig, string line) { rig.Send(line); Equal("ok", rig.Output.Last()); }
void Error(Rig rig, string line, int code) { rig.Send(line); Equal($"error:{code}", rig.Output.Last()); }

Test("exact boot banner and modal report", () => { var r=New(); r.Send("$G"); Equal("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]",r.Output[^2]); });
Test("all 34 default settings", () => { var r=New(); r.Send("$$"); Equal(34,r.Output.Count(s=>s.StartsWith('$'))); Equal("$100=250.000",r.Output.Single(s=>s.StartsWith("$100="))); });
Test("build information", () => { var r=New(); r.Send("$I"); Equal("[VER:1.1f.20170801:]",r.Output[^3]); Equal("[OPT:V,15,128]",r.Output[^2]); });
Test("help", () => { var r=New(); Ok(r,"$"); Equal(true,r.Output[^2].StartsWith("[HLP:")); });
Test("comments, lowercase, block delete", () => { var r=New(); Ok(r,"/g1 x10 f100 (ignored ! ? are realtime) ; note"); });
// Feed is still mandatory even if the axis value is zero.
Test("feed required", () => { var r=New(); Error(r,"G1X1",22); });
Test("linear feed motion", () => { var r=New(); Ok(r,"G1X10Y5F300"); Equal("Run",r.Engine.Snapshot().State); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); Near(5,r.Engine.Snapshot().MachinePosition.Y); });
Test("incremental", () => { var r=New(); Ok(r,"G91G0X5"); r.Finish(); Ok(r,"X5"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); });
Test("inch units", () => { var r=New(); Ok(r,"G20G0X1"); r.Finish(); Near(25.4,r.Engine.Snapshot().MachinePosition.X); });
Test("plane XY clockwise arc", () => { var r=New(); Ok(r,"G0X10"); r.Finish(); Ok(r,"G2X0Y-10I-10J0F300"); r.Finish(); Near(0,r.Engine.Snapshot().MachinePosition.X); Near(-10,r.Engine.Snapshot().MachinePosition.Y); });
Test("plane ZX arc", () => { var r=New(); Ok(r,"G0Z10"); r.Finish(); Ok(r,"G18G3Z0X10K-10I0F300"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); Near(0,r.Engine.Snapshot().MachinePosition.Z); });
Test("plane YZ arc", () => { var r=New(); Ok(r,"G0Y10"); r.Finish(); Ok(r,"G19G3Y0Z10J-10K0F300"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.Z); });
Test("radius arcs and negative radius", () => { var r=New(); Ok(r,"G2X10Y10R10F300"); r.Finish(); Ok(r,"G3X0Y0R-10"); r.Finish(); Near(0,r.Engine.Snapshot().MachinePosition.X); });
Test("full circle with IJK and axis word", () => { var r=New(); Ok(r,"G0X10"); r.Finish(); Ok(r,"G3X10I-10F100"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); });
Test("G10 L2/G54/G55/G53", () => { var r=New(); Ok(r,"G10L2P2X10"); Ok(r,"G55G0X0"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); Near(0,r.Engine.Snapshot().WorkPosition.X); Ok(r,"G53G0X0"); r.Finish(); Near(0,r.Engine.Snapshot().MachinePosition.X); });
Test("G10 L20", () => { var r=New(); Ok(r,"G0X20"); r.Finish(); Ok(r,"G10L20P1X5"); Near(5,r.Engine.Snapshot().WorkPosition.X); });
Test("G92 and G92.1", () => { var r=New(); Ok(r,"G0X10"); r.Finish(); Ok(r,"G92X3"); Near(3,r.Engine.Snapshot().WorkPosition.X); Ok(r,"G92.1"); Near(10,r.Engine.Snapshot().WorkPosition.X); });
Test("G43.1 and G49 tool offset", () => { var r=New(); Ok(r,"G43.1Z5"); Ok(r,"G0Z0"); r.Finish(); Near(5,r.Engine.Snapshot().MachinePosition.Z); Ok(r,"G49"); Near(5,r.Engine.Snapshot().WorkPosition.Z); });
Test("G28.1/G28 and G30.1/G30", () => { var r=New(); Ok(r,"G0X10Y20"); r.Finish(); Ok(r,"G28.1"); Ok(r,"G30.1"); Ok(r,"G0X0Y0"); r.Finish(); r.Send("G28X5"); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); Near(0,r.Engine.Snapshot().MachinePosition.Y); r.Send("G30"); r.Finish(); Near(20,r.Engine.Snapshot().MachinePosition.Y); });
Test("dwell blocks acknowledgment", () => { var r=New(); r.Send("G4P1"); Equal(0,r.Output.Count); r.Engine.Tick(TimeSpan.FromSeconds(.5)); Equal(0,r.Output.Count); r.Engine.Tick(TimeSpan.FromSeconds(.6)); Equal("ok",r.Output.Last()); });
Test("inverse time feed", () => { var r=New(); Ok(r,"G93G1X10F60"); r.Engine.Tick(TimeSpan.FromSeconds(.5)); Equal("Run",r.Engine.Snapshot().State); r.Finish(); Near(10,r.Engine.Snapshot().MachinePosition.X); Error(r,"X20",22); });
Test("spindle/coolant and program end", () => { var r=New(); Ok(r,"M3S800"); Near(800,r.Engine.Snapshot().Spindle); Ok(r,"M8"); Equal(1,r.Engine.Snapshot().Coolant); Ok(r,"M4"); Equal(4,r.Engine.Snapshot().SpindleMode); r.Send("M30"); r.Finish(); Equal(5,r.Engine.Snapshot().SpindleMode); Equal(0,r.Engine.Snapshot().Coolant); Equal(true,r.Output.Contains("[MSG:Pgm End]")); });
Test("M0 pause M1 ignored M2 end", () => { var r=New(); Ok(r,"M1"); r.Send("M0"); r.Engine.Tick(TimeSpan.FromSeconds(.1)); Equal("Hold:0",r.Engine.Snapshot().State); r.Engine.Receive("~"); Equal("Idle",r.Engine.Snapshot().State); r.Send("M2"); r.Finish(); Equal(true,r.Output.Contains("[MSG:Pgm End]")); });
Test("optional mist disabled by default", () => { var r=New(); Error(r,"M7",20); var m=New(true); Ok(m,"M7"); Equal(2,m.Engine.Snapshot().Coolant); });
Test("check mode does not move", () => { var r=New(); Ok(r,"$C"); Ok(r,"G1X10F300"); r.Engine.Tick(TimeSpan.FromSeconds(20)); Near(0,r.Engine.Snapshot().MachinePosition.X); Equal("Check",r.Engine.Snapshot().State); r.Send("$C"); Equal("Idle",r.Engine.Snapshot().State); });
Test("hold freezes, resume continues", () => { var r=New(); Ok(r,"G1X100F300"); r.Engine.Tick(TimeSpan.FromSeconds(1)); var x=r.Engine.Snapshot().MachinePosition.X; r.Engine.Receive("!"); r.Engine.Tick(TimeSpan.FromSeconds(5)); Near(x,r.Engine.Snapshot().MachinePosition.X); r.Engine.Receive("~"); r.Finish(); Near(100,r.Engine.Snapshot().MachinePosition.X); });
Test("realtime query inside partial line", () => { var r=New(); r.Engine.Receive("G1X?10F300\n"); Equal(true,r.Output[0].StartsWith("<Idle|")); Equal("ok",r.Output[^1]); });
Test("reset during motion produces alarm", () => { var r=New(); Ok(r,"G1X100F300"); r.Engine.Receive(new byte[] { 0x18 }); Equal("Alarm",r.Engine.Snapshot().State); Equal(true,r.Output.Contains("ALARM:3")); Error(r,"G0X0",9); Ok(r,"$X"); });
Test("soft limits require homing", () => { var r=New(); Error(r,"$20=1",10); Ok(r,"$22=1"); Ok(r,"$20=1"); r.Send("G0X1"); Equal(true,r.Output.Contains("ALARM:2")); });
Test("homing cycle with enabled settings", () => { var r=New(); Error(r,"$H",5); Ok(r,"$22=1"); r.Send("$H"); Equal("Home",r.Engine.Snapshot().State); r.Finish(); Near(-1,r.Engine.Snapshot().MachinePosition.X); Equal("ok",r.Output.Last()); });
Test("hard limits and door", () => { var r=New(); Ok(r,"$21=1"); r.Engine.SetLimits("X"); Equal("Alarm",r.Engine.Snapshot().State); r.Engine.SetLimits(""); r.Engine.SetDoor(true); Error(r,"$X",13); r.Engine.SetDoor(false); Ok(r,"$X"); r.Engine.SetDoor(true); Equal("Door:1",r.Engine.Snapshot().State); r.Engine.SetDoor(false); r.Engine.Receive("~"); Equal("Idle",r.Engine.Snapshot().State); });
Test("G38.2 successful virtual probe", () => { var r=New(); r.Engine.SetProbePlane(-5); r.Send("G38.2Z-10F100"); r.Finish(); Near(-5,r.Engine.Snapshot().MachinePosition.Z); Equal(true,r.Output.Contains("[PRB:0.000,0.000,-5.000:1]")); Equal("ok",r.Output.Last()); });
Test("G38.3 no contact is not alarm", () => { var r=New(); r.Send("G38.3Z-10F100"); r.Finish(); Equal("Idle",r.Engine.Snapshot().State); Equal(true,r.Output.Contains("[PRB:0.000,0.000,-10.000:0]")); });
Test("G38.2 no contact alarm 5", () => { var r=New(); r.Send("G38.2Z-10F100"); r.Finish(); Equal("Alarm",r.Engine.Snapshot().State); Equal(true,r.Output.Contains("ALARM:5")); });
Test("probe initial state alarm 4", () => { var r=New(); r.Engine.SetProbe(true); r.Send("G38.2Z-10F100"); r.Finish(); Equal(true,r.Output.Contains("ALARM:4")); });
Test("G38.4 away probe", () => { var r=New(); r.Engine.SetProbe(true); r.Send("G38.4Z10F100"); r.Engine.Tick(TimeSpan.FromSeconds(.1)); r.Engine.SetProbe(false); r.Engine.Tick(TimeSpan.FromSeconds(.1)); Equal("Idle",r.Engine.Snapshot().State); Equal(true,r.Output.Any(s=>s.StartsWith("[PRB:")&&s.EndsWith(":1]"))); });
Test("G38.5 away without contact", () => { var r=New(); r.Engine.SetProbe(true); r.Send("G38.5Z10F100"); r.Finish(); Equal("Idle",r.Engine.Snapshot().State); });
Test("jog keeps modal modes and cancel syncs parser", () => { var r=New(); Ok(r,"G20G91"); Ok(r,"$J=G21G90X10F100"); r.Engine.Tick(TimeSpan.FromSeconds(.1)); r.Engine.Receive(new byte[] {0x85}); Equal("Idle",r.Engine.Snapshot().State); Equal(true,r.Engine.Snapshot().Parser.Contains("G20 G91")); Near(r.Engine.Snapshot().MachinePosition.X,r.Engine.Snapshot().PlannedPosition.X); Error(r,"$J=G17X10F100",16); });
Test("jog requires feed", () => { var r=New(); Error(r,"$J=X10",22); });
Test("feed spindle rapid override bytes", () => { var r=New(); r.Engine.Receive(new byte[] {0x91,0x93,0x96,0x9b}); var s=r.Engine.Snapshot(); Equal(111,s.FeedOverride); Equal(50,s.RapidOverride); Equal(90,s.SpindleOverride); r.Engine.Receive(new byte[] {0x90,0x95,0x99}); s=r.Engine.Snapshot(); Equal(100,s.FeedOverride); Equal(100,s.RapidOverride); Equal(100,s.SpindleOverride); });
Test("buffer streaming pauses acknowledgments when full", () => { var r=New(); for(int i=1;i<=16;i++) r.Engine.Receive($"G1X{i}F100\n"); Equal(15,r.Engine.Snapshot().Buffered); Equal(15,r.Output.Count(s=>s=="ok")); r.Engine.Receive("?"); Equal(true,r.Output.Last().StartsWith("<Run|")); r.Finish(); Near(16,r.Engine.Snapshot().MachinePosition.X); Equal(16,r.Output.Count(s=>s=="ok")); });
Test("line overflow and recovery", () => { var r=New(); Error(r,new string('X',80),11); Ok(r,"G0X1"); });
Test("status MPos/WPos/buffer/inch formatting", () => { var r=New(); Ok(r,"$10=2"); r.Engine.Receive("?"); Equal(true,r.Output.Last().Contains("|WPos:0.000,0.000,0.000|Bf:15,127|")); Ok(r,"$13=1"); r.Engine.Receive("?"); Equal(true,r.Output.Last().Contains("0.0000,0.0000,0.0000")); });
Test("settings persist across process instance", () => {
    var folder=Path.Combine(Path.GetTempPath(),"VirtualCncTest-"+Guid.NewGuid()); var file=Path.Combine(folder,"eeprom.json");
    try { var r=new Rig(file); Ok(r,"$100=123.5"); Ok(r,"G10L2P1X12"); Ok(r,"$N0=G21"); Ok(r,"$I=VIRTUAL"); var reloaded=new GrblController(file); Near(123.5,reloaded.Memory.Settings[100]); Near(12,reloaded.Memory.Coordinates[0].X); Equal("G21",reloaded.Memory.Startup[0]); Equal("VIRTUAL",reloaded.Memory.BuildInfo); }
    finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
});
Test("EEPROM corruption preserves backup", () => { var folder=Path.Combine(Path.GetTempPath(),"VirtualCncTest-"+Guid.NewGuid()); Directory.CreateDirectory(folder); var file=Path.Combine(folder,"eeprom.json"); try { File.WriteAllText(file,"invalid"); var e=new GrblController(file); Equal(true,e.MemoryWarning is not null); Equal(1,Directory.GetFiles(folder,"*.corrupt-*").Length); } finally { Directory.Delete(folder,true); } });
Test("G92 and TLO are volatile, WCS persists", () => { var r=New(); Ok(r,"G10L2P1X2"); Ok(r,"G92X4"); Ok(r,"G43.1Z3"); r.Engine.Receive(new byte[] {0x18}); Near(-2,r.Engine.Snapshot().WorkPosition.X); Near(0,r.Engine.Snapshot().WorkPosition.Z); });
Test("restore modes", () => { var r=New(); Ok(r,"$100=123"); Ok(r,"G10L2P1X5"); r.Send("$RST=#"); Near(123,r.Engine.Memory.Settings[100]); Near(0,r.Engine.Memory.Coordinates[0].X); r.Send("$RST=$"); Near(250,r.Engine.Memory.Settings[100]); });
Test("sleep until reset", () => { var r=New(); Ok(r,"$SLP"); Equal("Sleep",r.Engine.Snapshot().State); r.Engine.Receive(new byte[]{0x18}); Equal("Idle",r.Engine.Snapshot().State); });
Test("unsupported extended byte not added to line", () => { var r=New(); r.Engine.Receive(new byte[]{(byte)'G',0x98,(byte)'0',10}); Equal("ok",r.Output.Last()); });

foreach (var (code,line) in new (int,string)[] {
    (1,"1X0"),(2,"G"),(3,"$999=0"),(4,"F-1"),(6,"$0=2"),(20,"G81"),(20,"G90.1"),(20,"M6"),(20,"M56P1"),
    (21,"G20G21"),(21,"M3M5"),(23,"M3.1"),(24,"G0G92X0"),(25,"X1X2"),(26,"G92"),(27,"N10000001"),(28,"G4"),
    (29,"G10L2P7X0"),(30,"G2F100G53X1Y1I1"),(31,"G80X1"),(32,"G2Z1F100I1"),(33,"G2X0Y0R1F100"),
    (34,"G2X10Y0R1F100"),(35,"G2X1Y1F100"),(36,"G0X1P2"),(37,"G43.1X1"),(38,"T256")
}) Test($"error:{code} {line}",()=>Error(New(),line,code));

Test("rejected block is atomic", () => { var r=New(); var before=r.Engine.Snapshot().Parser; Error(r,"G20G91G0X1P2",36); Equal(before,r.Engine.Snapshot().Parser); Near(0,r.Engine.Snapshot().PlannedPosition.X); });
Test("coordinate systems G54 through G59", () => { var r=New(); for (int i=0;i<6;i++) { Ok(r,$"G10L2P{i+1}X{i+1}"); Ok(r,$"G{54+i}G0X0"); r.Finish(); Near(i+1,r.Engine.Snapshot().MachinePosition.X); } });
Test("G40 G61 G91.1 G80 accepted", () => { var r=New(); Ok(r,"G40G61G91.1G80"); });
Test("acceleration settings affect motion timing", () => { var slow=New(); var fast=New(); Ok(slow,"$120=1"); Ok(fast,"$120=100"); Ok(slow,"G1X100F500"); Ok(fast,"G1X100F500"); slow.Engine.Tick(TimeSpan.FromSeconds(1)); fast.Engine.Tick(TimeSpan.FromSeconds(1)); Equal(true,fast.Engine.Snapshot().MachinePosition.X>slow.Engine.Snapshot().MachinePosition.X+3); });
Test("laser rapid blanking and dynamic power", () => { var r=New(); Ok(r,"$32=1"); Ok(r,"M3S800"); Near(0,r.Engine.Snapshot().Spindle); Ok(r,"G0X10"); r.Engine.Tick(TimeSpan.FromSeconds(.1)); Near(0,r.Engine.Snapshot().Spindle); r.Finish(); Ok(r,"G1X20F300M4"); r.Engine.Tick(TimeSpan.FromSeconds(.1)); Equal(true,r.Engine.Snapshot().Spindle>0 && r.Engine.Snapshot().Spindle<800); r.Finish(); Near(0,r.Engine.Snapshot().Spindle); });
Test("homing reset alarm 6", () => { var r=New(); Ok(r,"$22=1"); r.Send("$H"); r.Engine.Receive(new byte[]{0x18}); Equal(true,r.Output.Contains("ALARM:6")); });
Test("homing door alarm 7", () => { var r=New(); Ok(r,"$22=1"); r.Send("$H"); r.Engine.SetDoor(true); Equal(true,r.Output.Contains("ALARM:7")); });
Test("homing pull-off failure alarm 8", () => { var r=New(); Ok(r,"$22=1"); r.Engine.SetLimits("X"); r.Send("$H"); r.Finish(); Equal(true,r.Output.Contains("ALARM:8")); });
Test("homing contact failure alarm 9", () => { var r=New(); Ok(r,"$22=1"); r.Engine.HomingSwitchContact=false; r.Send("$H"); r.Finish(); Equal(true,r.Output.Contains("ALARM:9")); });
Test("arc extrema checked against soft travel limits", () => { var r=New(); Ok(r,"$22=1"); Ok(r,"$20=1"); Ok(r,"G0X-10Y-10"); r.Finish(); r.Send("G2X-10Y-10I15F100"); Equal(true,r.Output.Contains("ALARM:2")); });
Test("homing switches trip at configured dimensions and pull off", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$23=7"); Ok(r,"$130=12"); Ok(r,"$131=23"); Ok(r,"$132=7"); Ok(r,"$27=0.5");
    var contacts=new List<(string Axes,Vec Position)>(); r.Engine.LimitsChanged+=axes=>contacts.Add((axes,r.Engine.Snapshot().MachinePosition));
    r.Send("$H"); r.Finish(); Equal("Idle",r.Engine.Snapshot().State); Equal("ok",r.Output.Last());
    Near(-11.5,r.Engine.Snapshot().MachinePosition.X); Near(-22.5,r.Engine.Snapshot().MachinePosition.Y); Near(-6.5,r.Engine.Snapshot().MachinePosition.Z);
    foreach(var (axis,boundary) in new[]{(0,-12.0),(1,-23.0),(2,-7.0)}) {
        var hits=contacts.Where(c=>c.Axes.Contains("XYZ"[axis])).ToArray(); Equal(true,hits.Length>=2); foreach(var hit in hits) Near(boundary,hit.Position[axis]);
    }
    Equal(true,contacts.First(c=>c.Axes.Length>0).Axes=="Z"); Equal("",r.Engine.Snapshot().Limits);
});
Test("homing switch is reported in realtime Pn and debounce holds contact", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$26=1000"); r.Send("$H"); r.Engine.Tick(TimeSpan.FromSeconds(.1));
    Equal("Z",r.Engine.Snapshot().Limits); r.Engine.Receive("?"); Equal(true,r.Output.Last().Contains("|Pn:Z"));
    r.Engine.Tick(TimeSpan.FromSeconds(.5)); Equal("Z",r.Engine.Snapshot().Limits); r.Finish(); Equal("",r.Engine.Snapshot().Limits);
});
Test("mixed homing directions choose independent boundaries", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$23=5"); Ok(r,"$130=10"); Ok(r,"$131=20"); Ok(r,"$132=30");
    Ok(r,"G0X-5Y-5Z-5"); r.Finish(); var hits=new List<(string Axes,Vec Position)>(); r.Engine.LimitsChanged+=axes=>hits.Add((axes,r.Engine.Snapshot().MachinePosition));
    r.Send("$H"); r.Finish(); Near(-9,r.Engine.Snapshot().MachinePosition.X); Near(-1,r.Engine.Snapshot().MachinePosition.Y); Near(-29,r.Engine.Snapshot().MachinePosition.Z);
    Equal(true,hits.Any(h=>h.Axes.Contains('Y')&&Math.Abs(h.Position.Y)<.005));
});
Test("hard limit alarm is suppressed during homing contacts", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$21=1"); r.Send("$H"); r.Finish(); Equal("Idle",r.Engine.Snapshot().State); Equal(false,r.Output.Contains("ALARM:1"));
});
Test("slow homing advances below one step per tick", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$24=0.6"); Ok(r,"$27=0.02"); Ok(r,"$26=0"); r.Send("$H");
    for(int i=0;i<15000 && r.Engine.Snapshot().State=="Home";i++) r.Engine.Tick(TimeSpan.FromMilliseconds(1));
    Equal("Idle",r.Engine.Snapshot().State); Near(-.02,r.Engine.Snapshot().MachinePosition.X);
});
Test("each homing axis stops as soon as its switch is reached", () => {
    var r=New(); Ok(r,"$22=1"); Ok(r,"$23=7"); Ok(r,"$130=2"); Ok(r,"$131=12"); Ok(r,"$132=1"); Ok(r,"$26=500");
    r.Send("$H"); bool independent=false;
    for(int i=0;i<2000 && r.Engine.Snapshot().State=="Home";i++) {
        r.Engine.Tick(TimeSpan.FromSeconds(.02)); var s=r.Engine.Snapshot();
        if(s.Limits.Contains('X')&&!s.Limits.Contains('Y')) { Near(-2,s.MachinePosition.X); Equal(true,s.MachinePosition.Y>-12); independent=true; }
    }
    Equal(true,independent); Equal("Idle",r.Engine.Snapshot().State);
});
Test("XY viewport fits configured travel inside canvas",()=>{
    var v=new XYViewport(); v.SetSize(800,400); v.SetTravel(530,350);
    var min=v.Project(new(-530,-350,0)); var max=v.Project(default);
    Equal(true,min.X>=0 && max.X<=800 && max.Y>=0 && min.Y<=400);
    Near(530.0/350,(max.X-min.X)/(min.Y-max.Y),.00001);
});
Test("XY wheel zoom keeps point under cursor fixed",()=>{
    var v=new XYViewport(); v.SetSize(800,400); v.SetTravel(530,350);
    var anchor=v.Unproject(235,182); v.ZoomAt(235,182,1.2); var projected=v.Project(anchor);
    Near(235,projected.X,.000001); Near(182,projected.Y,.000001); Near(1.2,v.Zoom);
});
Test("snapshot carries actual configured travel",()=>{var r=New(); Ok(r,"$130=530"); Ok(r,"$131=350"); Equal(new Vec(530,350,200),r.Engine.Snapshot().MaxTravel);});
Test("hard reset loses position but preserves EEPROM and physical inputs",()=>{
    var r=New();Ok(r,"$130=530");Ok(r,"G0X-20Y-10");r.Finish();Ok(r,"G92X0Y0");r.Engine.SetProbe(true);
    r.Engine.HardReset();var s=r.Engine.Snapshot();Equal(default(Vec),s.MachinePosition);Equal(default(Vec),s.WorkPosition);Equal(true,s.Probe);Equal(530.0,r.Engine.Memory.Settings[130]);Equal("Idle",s.State);Equal(true,r.Output.Contains(GrblController.Banner));
});
Test("hard reset requires homing again when enabled",()=>{var r=New();Ok(r,"$22=1");r.Engine.HardReset();Equal("Alarm",r.Engine.Snapshot().State);});
Test("motion segments use executed block G0/G1 and retain shared endpoints",()=>{
    var r=New();var segments=new List<MotionSegment>();r.Engine.MotionAdvanced+=segments.Add;
    Ok(r,"G0X-10");Ok(r,"G1X-20F100");r.Finish();
    Equal(true,segments.Any(s=>s.Rapid));Equal(true,segments.Any(s=>!s.Rapid));
    var firstFeed=segments.First(s=>!s.Rapid);Near(-10,firstFeed.Start.X,.01);
    for(int i=1;i<segments.Count;i++) Equal(segments[i-1].End,segments[i].Start);
});
Test("table XY origin is lower left and positive work moves remain inside travel",()=>{
    var v=new XYViewport();v.SetSize(800,400);v.SetTravel(530,350);
    var lowerLeft=v.ProjectTable(default);var upperRight=v.ProjectTable(new(530,350,0));var point=v.ProjectTable(new(120,150,0));
    Equal(v.Project(new(-530,-350,0)),lowerLeft);Equal(v.Project(default),upperRight);
    Equal(true,point.X>lowerLeft.X && point.X<upperRight.X && point.Y<lowerLeft.Y && point.Y>upperRight.Y);
    var back=v.UnprojectTable(point.X,point.Y);Near(120,back.X,.000001);Near(150,back.Y,.000001);
});
Test("table G54 origin yields pull-off work XY after mixed-direction home",()=>{
    var r=New();Ok(r,"$130=530");Ok(r,"$131=350");Ok(r,"$132=120");Ok(r,"$23=3");Ok(r,"$27=5");Ok(r,"$22=1");r.Engine.InitializeTableWorkOrigin();r.Send("$H");r.Finish();Equal("ok",r.Output.Last());
    var s=r.Engine.Snapshot();Equal(new Vec(-525,-345,-5),s.MachinePosition);Equal(new Vec(5,5,-5),s.WorkPosition);
    r.Send("?");Equal(true,r.Output.Any(line=>line.Contains("WCO:-530.000,-350.000,0.000")));
    Ok(r,"G0X120Y150");r.Finish();Equal(new Vec(-410,-200,-5),r.Engine.Snapshot().MachinePosition);
});
Test("table origin setup preserves Z and subsequent custom G54 and G92",()=>{
    var r=New();Ok(r,"G10L2P1Z-75");r.Engine.InitializeTableWorkOrigin();Equal(new Vec(-200,-200,-75),r.Engine.Memory.Coordinates[0]);
    Ok(r,"G10L2P1X-100Y-80");Ok(r,"G92X0Y0");r.Engine.InitializeTableWorkOrigin();Equal(new Vec(-100,-80,-75),r.Engine.Memory.Coordinates[0]);Equal(0.0,r.Engine.Snapshot().WorkPosition.X);
});
Test("table origin setup persists once and survives hard reset",()=>{
    var file=Path.Combine(Path.GetTempPath(),"VirtualCncOrigin-"+Guid.NewGuid()+".json");
    try {var r=new Rig(file);r.Engine.InitializeTableWorkOrigin();Ok(r,"G10L2P1X-90Y-70");var next=new Rig(file);next.Engine.InitializeTableWorkOrigin();next.Engine.HardReset();Equal(new Vec(-90,-70,0),next.Engine.Memory.Coordinates[0]);}finally{if(File.Exists(file))File.Delete(file);}
});
Console.WriteLine($"\n{passed} passed; {failed} failed");
return failed==0 ? 0 : 1;

internal sealed class Rig
{
    public GrblController Engine { get; }
    public List<string> Output { get; } = [];
    public Rig(string? path=null,bool mist=false) { Engine=new(path,mist); Engine.Output+=Output.Add; Engine.Boot(); Output.Clear(); }
    public void Send(string text) { Output.Clear(); Engine.Receive(text+"\n"); }
    public void Finish() { for(int i=0;i<2000 && Engine.Snapshot().State is "Run" or "Jog" or "Home";i++) Engine.Tick(TimeSpan.FromSeconds(.1)); }
}
