using System.Globalization;
using System.Text;

namespace VirtualCnc.Core;

/// <summary>Byte-oriented GRBL 1.1f protocol and deterministic virtual three-axis machine.</summary>
public sealed partial class GrblController
{
    public const string Banner = "Grbl 1.1f ['$' for help]";
    public const int PlannerCapacity = 15, ReceiveCapacity = 127;
    private readonly object gate = new();
    private readonly MemoryStore store;
    private readonly Queue<byte> receive = new();
    private readonly Queue<MotionBlock> planner = new();
    private readonly StringBuilder line = new();
    private Modal modal = new();
    private Vec position, probePosition;
    private Vec physicalOffset;
    private Vec PhysicalPosition => position + physicalOffset;
    private bool comment, semicolon, overflow, check, probeSuccess, booted, spindleStop, deferredAck;
    private string? pending;
    private string state = "Idle", limits = "";
    private int feedOverride = 100, rapidOverride = 100, spindleOverride = 100, statusCounter;
    private int actualSpindle = 5, actualCoolant;
    private double actualSpeed, currentFeed, velocity;
    private bool laserMotion, awaitingInitialHome;
    private bool probePin, doorPin;
    private double? probePlane;
    private bool materialProbeEnabled;
    public bool EnableMist { get; }
    public bool HomingSwitchContact { get; set; } = true;
    public double TimeScale { get; set; } = 1;
    public Eeprom Memory { get; private set; }
    public string? MemoryWarning => store.LoadWarning;
    public event Action<string>? Output;
    public event Action<Vec>? PositionChanged;
    public event Action<MotionSegment>? MotionAdvanced;
    public event Action<string>? LimitsChanged;

    public GrblController(string? memoryPath = null, bool enableMist = false)
    {
        EnableMist = enableMist;
        store = new(memoryPath);
        Memory = store.Load();
    }
    private double Setting(int key) => Memory.Settings[key];
    private bool Inches => Setting(13) != 0;
    private Vec ActiveCoordinate(int index) => awaitingInitialHome ? default : Memory.Coordinates[index];
    private Vec Wco => ActiveCoordinate(modal.Coordinate - 54) + modal.Offset + new Vec(0, 0, modal.ToolLength);
    private void Emit(string text) => Output?.Invoke(text);
    private void Reply(int error = 0) => Emit(error == 0 ? "ok" : $"error:{error}");
    private void Save() => store.Save(Memory);

    /// <summary>One-time application setup of G54 at the lower-left corner; Z is retained.</summary>
    public void InitializeTableWorkOrigin()
    {
        lock(gate)
        {
            if(Memory.TableWorkOriginInitialized) return;
            Memory.Coordinates[0]=new(-Setting(130),-Setting(131),Memory.Coordinates[0].Z);
            Memory.TableWorkOriginInitialized=true;Save();
        }
    }
    public void Boot()
    {
        lock (gate)
        {
            if(!booted) {position=default;awaitingInitialHome=Memory.TableWorkOriginInitialized;physicalOffset=awaitingInitialHome ? new(-Setting(130)/2,-Setting(131)/2,-Setting(132)/2) : default;}
            Reset(false, true);
            if (MemoryWarning is not null) { Reply(7); foreach (var setting in Memory.Settings) EmitSetting(setting.Key, setting.Value); }
            booted = true;
        }
    }
    /// <summary>MCU restart: volatile machine position is lost; EEPROM and physical inputs survive.</summary>
    public void HardReset()
    {
        lock(gate)
        {
            physicalOffset=Memory.TableWorkOriginInitialized ? PhysicalPosition : default;
            position=probePosition=default;probeSuccess=false;state="Idle";statusCounter=0;
            travelLimits="";awaitingInitialHome=Memory.TableWorkOriginInitialized;Reset(false,true);booted=true;PositionChanged?.Invoke(position);
        }
    }
    private void Reset(bool interrupted, bool cold = false)
    {
        var wasAlarm = state == "Alarm";
        var wasHoming = state == "Home";
        homing = null; homingLimits = "";
        receive.Clear(); planner.Clear(); pending = null; line.Clear();
        comment = semicolon = overflow = check = deferredAck = spindleStop = false;
        feedOverride = rapidOverride = spindleOverride = 100;
        currentFeed = actualSpeed = velocity = 0; actualSpindle = 5; actualCoolant = 0;
        modal = new Modal { Position = position };
        state = interrupted || wasAlarm || (cold && Setting(22) != 0) ? "Alarm" : "Idle";
        Emit(""); Emit(Banner);
        if (interrupted) Emit(wasHoming ? "ALARM:6" : "ALARM:3");
        if (state == "Alarm") Emit("[MSG:'$H'|'$X' to unlock]");
        else RunStartup();
    }
    private void RunStartup()
    {
        foreach (var startup in Memory.Startup.Where(s => s.Length != 0))
        {
            try { Commit(Parse(startup)); Emit($">{startup}:ok"); }
            catch (GrblError error) { Emit($">{startup}:error:{error.Code}"); }
        }
    }
    public void Receive(string text) => Receive(Encoding.ASCII.GetBytes(text));
    public void Receive(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            foreach (var b in bytes)
            {
                if (Realtime(b)) continue;
                // AVR's receive ISR silently drops bytes when the 128-byte ring fills.
                if (receive.Count < ReceiveCapacity) receive.Enqueue(b);
                Pump();
            }
        }
    }
    private bool Realtime(byte b)
    {
        switch (b)
        {
            case 0x18: Reset(planner.Count > 0 || state is "Jog" or "Home"); return true;
            case (byte)'?': ReportStatus(); return true;
            case (byte)'!':
                if (state is "Run" or "Jog" or "Idle") { state = "Hold:0"; currentFeed = velocity = 0; }
                return true;
            case (byte)'~':
                if (!doorPin && (state.StartsWith("Hold", StringComparison.Ordinal) || state.StartsWith("Door", StringComparison.Ordinal)))
                {
                    state = planner.Count == 0 ? "Idle" : planner.Peek().Jog ? "Jog" : "Run";
                    spindleStop = false;
                }
                return true;
            case 0x84: // Safety door
                if (state == "Home") { Alarm(7); return true; }
                if (state is not ("Alarm" or "Sleep" or "Check")) { state = doorPin ? "Door:1" : "Door:0"; currentFeed = velocity = 0; }
                return true;
            case 0x85: // Jog cancel
                if (state == "Jog" || planner.Any(m => m.Jog))
                { planner.Clear(); modal.Position = position; currentFeed = velocity = 0; state = "Idle"; }
                return true;
            case 0x90: feedOverride = 100; return true;
            case 0x91: feedOverride = Math.Min(200, feedOverride + 10); return true;
            case 0x92: feedOverride = Math.Max(10, feedOverride - 10); return true;
            case 0x93: feedOverride = Math.Min(200, feedOverride + 1); return true;
            case 0x94: feedOverride = Math.Max(10, feedOverride - 1); return true;
            case 0x95: rapidOverride = 100; return true;
            case 0x96: rapidOverride = 50; return true;
            case 0x97: rapidOverride = 25; return true;
            case 0x99: spindleOverride = 100; return true;
            case 0x9a: spindleOverride = Math.Min(200, spindleOverride + 10); return true;
            case 0x9b: spindleOverride = Math.Max(10, spindleOverride - 10); return true;
            case 0x9c: spindleOverride = Math.Min(200, spindleOverride + 1); return true;
            case 0x9d: spindleOverride = Math.Max(10, spindleOverride - 1); return true;
            case 0x9e:
                if (state.StartsWith("Hold", StringComparison.Ordinal)) spindleStop = !spindleStop;
                return true;
            case 0xa0: actualCoolant ^= 1; return true;
            case 0xa1: if (EnableMist) actualCoolant ^= 2; return true;
            default: return b > 0x7f; // Unsupported extended commands are swallowed, like the ISR.
        }
    }
    private void Pump()
    {
        if (deferredAck) return;
        while (true)
        {
            if (pending is not null)
            {
                if (!Execute(pending)) return;
                pending = null;
                if (deferredAck) return;
            }
            if (!receive.TryDequeue(out var b)) return;
            if (b is 10 or 13)
            {
                if (overflow) { Reply(11); line.Clear(); }
                else pending = line.ToString();
                line.Clear(); comment = semicolon = overflow = false;
            }
            else if (!overflow)
            {
                if (b == ')') { comment = false; continue; }
                if (comment || semicolon) continue;
                if (b == '(') { comment = true; continue; }
                if (b == ';') { semicolon = true; continue; }
                if (b <= ' ' || b == '/') continue;
                if (line.Length == 79) { overflow = true; continue; }
                line.Append(char.ToUpperInvariant((char)b));
            }
        }
    }
    private bool Execute(string text)
    {
        if (text.Length == 0) { Reply(); return true; }
        if (state == "Sleep") return false;
        try
        {
            if (text[0] == '$') return SystemCommand(text);
            Require(state is not ("Alarm" or "Jog"), 9);
            var parsed = Parse(text);
            if (!check && ((parsed.Sync && planner.Count != 0) || planner.Count + parsed.Blocks.Count > PlannerCapacity)) return false;
            if (!Commit(parsed)) return true;
            if (!check && parsed.Blocks.Any(b => b.Probe || b.Action is "dwell" or "pause" or "end")) deferredAck = true;
            if (!deferredAck) Reply();
        }
        catch (GrblError error) { Reply(error.Code); }
        return true;
    }
    private bool Commit(Parsed parsed, bool jog = false)
    {
        if (!check)
        {
            foreach (var block in parsed.Blocks)
            {
                if (Setting(20) != 0 && MotionOutside(block)) { Alarm(2); return false; }
            }
        }
        foreach (var write in parsed.Writes) write();
        if (parsed.Writes.Count > 0) Save();
        if (jog) modal.Position = parsed.Modal.Position;
        else modal = parsed.Modal;
        if (parsed.Sync) statusCounter = 0;
        if (check) return true;
        if (planner.Count == 0 && parsed.Blocks.Count == 0)
        { actualSpindle = modal.Spindle; actualSpeed = modal.Speed; actualCoolant = modal.Coolant; laserMotion = modal.Motion is 1 or 2 or 3; }
        foreach (var block in parsed.Blocks) planner.Enqueue(block);
        if (planner.Count != 0 && state == "Idle") state = jog ? "Jog" : "Run";
        return true;
    }
    private bool Outside(Vec target) => Enumerable.Range(0, 3).Any(i => target[i] > 1e-9 || target[i] < -Setting(130 + i) - 1e-9);
    private bool MotionOutside(MotionBlock block)
    {
        if (Outside(block.End)) return true;
        if (block.Radius == 0) return false;
        var start = Math.Atan2(block.Start[block.Axis1]-block.Center[block.Axis1],block.Start[block.Axis0]-block.Center[block.Axis0]);
        for (int quadrant=0;quadrant<4;quadrant++)
        {
            var delta = quadrant*Math.PI/2-start;
            while (delta < 0) delta += 2*Math.PI;
            while (delta >= 2*Math.PI) delta -= 2*Math.PI;
            if (block.Sweep < 0 && delta > 0) delta -= 2*Math.PI;
            var progress=delta/block.Sweep;
            if (progress is >= 0 and <= 1 && Outside(block.At(progress))) return true;
        }
        return false;
    }
    private void Alarm(int code)
    {
        homing = null; homingLimits = "";
        planner.Clear(); modal.Position = position; deferredAck = false;
        currentFeed = actualSpeed = velocity = 0; actualSpindle = 5; actualCoolant = 0;
        state = "Alarm"; Emit($"ALARM:{code}");
    }
    private void EmitSetting(int id, double value)
    {
        bool integer = id is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 10 or 13 or 20 or 21 or 22 or 23 or 26 or 32;
        Emit($"${id}={value.ToString(integer ? "F0" : "F3", CultureInfo.InvariantCulture)}");
    }
    private bool SystemCommand(string text)
    {
        if (text == "$") { Emit("[HLP:$$ $# $G $I $N $x=val $Nx=line $J=line $SLP $C $X $H ~ ! ? ctrl-x]"); Reply(); return true; }
        if (text == "$G") { Emit(ParserReport()); Reply(); return true; }
        if (text == "$$")
        { Require(state != "Run" && !state.StartsWith("Hold", StringComparison.Ordinal), 8); foreach (var pair in Memory.Settings) EmitSetting(pair.Key, pair.Value); Reply(); return true; }
        if (text == "$C")
        {
            if (check) { Emit("[MSG:Disabled]"); Reset(false); }
            else { Require(state == "Idle", 8); check = true; state = "Check"; Emit("[MSG:Enabled]"); Reply(); }
            return true;
        }
        if (text == "$X")
        {
            if (state == "Alarm") { Require(!doorPin, 13); state = "Idle"; Emit("[MSG:Caution: Unlocked]"); }
            Reply(); return true;
        }
        if (text.StartsWith("$J=", StringComparison.Ordinal))
        {
            Require(state is "Idle" or "Jog", 8);
            var parsed = Parse(text[3..], true);
            Require(parsed.Blocks.All(b => !Outside(b.End)) || Setting(20) == 0, 15);
            if (planner.Count + parsed.Blocks.Count > PlannerCapacity) return false;
            Commit(parsed, true); Reply(); return true;
        }
        Require(state is "Idle" or "Alarm", 8);
        switch (text)
        {
            case "$#":
                for (int i = 0; i < 8; i++) Emit($"[G{(i < 6 ? 54 + i : i == 6 ? 28 : 30)}:{Memory.Coordinates[i].Format(Inches)}]");
                Emit($"[G92:{modal.Offset.Format(Inches)}]");
                Emit($"[TLO:{(modal.ToolLength / (Inches ? 25.4 : 1)).ToString(Inches ? "F4" : "F3", CultureInfo.InvariantCulture)}]");
                EmitProbe(); break;
            case "$I":
                Emit($"[VER:1.1f.20170801:{Memory.BuildInfo}]");
                Emit($"[OPT:V{(EnableMist ? "M" : "")},15,128]"); break;
            case "$N": for (int i = 0; i < 2; i++) Emit($"$N{i}={Memory.Startup[i]}"); break;
            case "$H":
                Require(Setting(22) != 0, 5); Require(!doorPin, 13);
                BeginHoming();
                deferredAck = true; return true;
            case "$SLP": state = "Sleep"; actualSpindle = 5; actualCoolant = 0; Emit("[MSG:Sleeping]"); break;
            case "$RST=$": case "$RST=#": case "$RST=*":
                if (text[^1] is '$' or '*') Memory.Settings = Eeprom.Defaults();
                if (text[^1] is '#' or '*') Memory.Coordinates = new Vec[8];
                if (text[^1] == '*') { Memory.Startup = ["", ""]; Memory.BuildInfo = ""; }
                Save(); Emit("[MSG:Restoring defaults]"); Reset(false); return true;
            default:
                if (text.StartsWith("$I=", StringComparison.Ordinal)) { Memory.BuildInfo = text[3..]; Save(); break; }
                if (text.StartsWith("$N", StringComparison.Ordinal))
                {
                    Require(state == "Idle", 8);
                    Require(text.Length >= 4 && text[2] is '0' or '1' && text[3] == '=', 3);
                    var startup = text[4..]; var parsed = Parse(startup);
                    if (planner.Count != 0) return false;
                    Commit(parsed); Memory.Startup[text[2] - '0'] = startup; Save(); break;
                }
                var split = text[1..].Split('=');
                Require(split.Length == 2, 3);
                Require(double.TryParse(split[0], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id), 2);
                Require(id is >= 0 and <= 255, 3);
                Require(double.TryParse(split[1], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value), 2);
                StoreSetting((int)id, value); break;
        }
        Reply(); return true;
    }
    private void StoreSetting(int id, double value)
    {
        Require(value >= 0, 4); Require(Memory.Settings.ContainsKey(id), 3);
        if (id == 0) Require(value >= 3, 6);
        if (id == 20 && value != 0) Require(Setting(22) != 0, 10);
        // MAX_STEP_RATE_HZ is disabled in the stock 1.1f configuration.
        if (id is 4 or 5 or 6 or 13 or 20 or 21 or 22 or 32) value = (int)value != 0 ? 1 : 0;
        else if (id == 26) value = (ushort)(int)value;
        else if (id is 0 or 1 or 2 or 3 or 10 or 23) value = (byte)(int)value;
        if(awaitingInitialHome && id is >=130 and <=132)
            physicalOffset=physicalOffset.With(id-130,Setting(id)>0 ? physicalOffset[id-130]*value/Setting(id) : -value/2);
        Memory.Settings[id] = value;
        if (id == 22 && value == 0) Memory.Settings[20] = 0;
        Save();
    }
    private string ParserReport()
    {
        var report = modal.Report(Inches);
        return report.Replace("G431", "G43.1", StringComparison.Ordinal);
    }
    private void EmitProbe() => Emit($"[PRB:{probePosition.Format(Inches)}:{(probeSuccess ? 1 : 0)}]");
    private double SpindleSpeed
    {
        get
        {
            if (actualSpindle == 5 || spindleStop || state.StartsWith("Door", StringComparison.Ordinal) || state == "Sleep" || actualSpeed == 0) return 0;
            var speed = actualSpeed * spindleOverride / 100;
            if (Setting(32) != 0)
            {
                if (!laserMotion) return 0;
                if (actualSpindle == 4) speed *= planner.TryPeek(out var block) && block.Feed > 0 ? currentFeed / block.Feed : 0;
            }
            return speed == 0 ? 0 : Math.Clamp(speed, Setting(31), Math.Max(Setting(31), Setting(30)));
        }
    }
    public void ReportStatus()
    {
        lock (gate)
        {
            var mask = (int)Setting(10);
            var text = new StringBuilder($"<{state}|{(mask.HasFlag(1) ? "MPos" : "WPos")}:{(mask.HasFlag(1) ? position : position - Wco).Format(Inches)}");
            if (mask.HasFlag(2)) text.Append($"|Bf:{PlannerCapacity - planner.Count},{ReceiveCapacity - receive.Count}");
            text.Append($"|FS:{(currentFeed / (Inches ? 25.4 : 1)).ToString(Inches ? "F1" : "F0", CultureInfo.InvariantCulture)},{SpindleSpeed.ToString("F0", CultureInfo.InvariantCulture)}");
            var pins = (ProbeAt(position) ? "P" : "") + EffectiveLimits + (doorPin ? "D" : "");
            if (pins.Length != 0) text.Append("|Pn:" + pins);
            if (statusCounter++ % (state is "Run" or "Jog" ? 30 : 10) == 0) text.Append("|WCO:" + Wco.Format(Inches));
            else
            {
                text.Append($"|Ov:{feedOverride},{rapidOverride},{spindleOverride}");
                var accessory = (SpindleSpeed > 0 ? actualSpindle == 4 ? "C" : "S" : "") + (actualCoolant.HasFlag(1) ? "F" : "") + (actualCoolant.HasFlag(2) ? "M" : "");
                if (accessory.Length != 0) text.Append("|A:" + accessory);
            }
            text.Append('>'); Emit(text.ToString());
        }
    }
    public KeyValuePair<int,double>[] SettingsSnapshot() { lock(gate) return Memory.Settings.ToArray(); }
    public MachineSnapshot Snapshot()
    {
        lock (gate) return new(state, position, position - Wco, modal.Position, currentFeed, SpindleSpeed, feedOverride, rapidOverride, spindleOverride,
            planner.Count, ProbeAt(position), EffectiveLimits, doorPin, actualSpindle, actualCoolant, ParserReport(), new Vec(Setting(130),Setting(131),Setting(132)),Math.Clamp(Memory.MaterialThickness,0,Setting(132)),materialProbeEnabled,SurfaceDeltaAt(position),PhysicalPosition);
    }
    public void ConfigureWorkpiece(double thickness,bool probingEnabled)
    {
        lock(gate)
        {
            if(!double.IsFinite(thickness) || thickness<0 || thickness>Setting(132)) throw new ArgumentOutOfRangeException(nameof(thickness));
            Memory.MaterialThickness=thickness;Memory.MaterialProbeEnabled=probingEnabled;materialProbeEnabled=probingEnabled;Save();
        }
    }
    public SurfaceProfile SurfaceSnapshot(){lock(gate)return Memory.Surface.Copy();}
    public void ConfigureSurface(SurfaceProfile profile)
    {
        lock(gate)
        {
            if(!Enum.IsDefined(profile.Coordinates) || profile.Columns<2 || profile.Columns>21 || profile.Rows<2 || profile.Rows>21 || profile.Values.Length!=profile.Columns*profile.Rows || !double.IsFinite(profile.X+profile.Y+profile.Width+profile.Height+profile.Amplitude) || profile.Width<0 || profile.Height<0 || profile.Width>Setting(130) || profile.Height>Setting(131) || profile.Amplitude<0 || profile.Amplitude>10 || profile.Values.Any(v=>!double.IsFinite(v) || Math.Abs(v)>profile.Amplitude+1e-9))throw new ArgumentOutOfRangeException(nameof(profile));
            Memory.Surface=profile.Copy();Save();
        }
    }
    private double SurfaceDeltaAt(Vec p)
    {
        var origin=Memory.Surface.Coordinates switch {SurfaceCoordinates.Work=>Wco,SurfaceCoordinates.Machine=>default(Vec),_=> (physicalOffset+new Vec(Setting(130),Setting(131),0)) * -1};
        var point=p-origin;
        var maximum=Memory.Surface.Coordinates==SurfaceCoordinates.Table ? new Vec(Setting(130),Setting(131),0) : (physicalOffset+origin)*-1;
        return Memory.Surface.Delta(point.X,point.Y,maximum.X,maximum.Y);
    }
    private double? ProbeSurface(Vec p) => materialProbeEnabled ? -Setting(132)-physicalOffset.Z+Math.Clamp(Memory.MaterialThickness+SurfaceDeltaAt(p),0,Setting(132)) : probePlane;
    public void SetProbe(bool active) { lock (gate) probePin = active; }
    public void SetProbePlane(double? machineZ) { lock (gate) probePlane = machineZ; }
    public void SetLimits(string activeAxes)
    {
        lock (gate)
        {
            limits = string.Concat("XYZ".Where(activeAxes.Contains));
            LimitsChanged?.Invoke(EffectiveLimits);
            if (EffectiveLimits.Length > 0 && Setting(21) != 0 && state != "Home") Alarm(1);
        }
    }
    public void SetDoor(bool open)
    {
        lock (gate)
        {
            doorPin = open;
            if (open) Realtime(0x84);
            else if (state.StartsWith("Door", StringComparison.Ordinal)) state = "Door:0";
        }
    }
    public void Tick(TimeSpan elapsed)
    {
        lock (gate)
        {
            if (!booted) return;
            var seconds = Math.Max(0, elapsed.TotalSeconds) * Math.Clamp(TimeScale, 0.01, 1000);
            if (state == "Home") AdvanceHoming(seconds);
            else if (state is "Run" or "Jog") Advance(seconds);
            Pump();
        }
    }
    private void Advance(double seconds)
    {
        while (seconds > 0 && planner.TryPeek(out var block))
        {
            if (!block.Started)
            {
                block.Started = true;
                if (block.Action is null) { actualSpindle = block.SpindleMode; actualSpeed = block.Speed; actualCoolant = block.Coolant; laserMotion = !block.Rapid && !block.Probe; }
                if (block.Probe)
                {
                    probeSuccess = false;
                    var initial = ProbeAt(position);
                    if (initial == !block.ProbeAway) { Alarm(4); Reply(); return; }
                }
            }
            if (block.Action is "pause" or "end")
            {
                planner.Dequeue(); currentFeed = velocity = 0;
                if (block.Action == "pause") { state = "Hold:0"; modal.Program = 0; }
                else
                {
                    state = "Idle"; modal.Motion = 1; modal.Plane = 17; modal.Distance = 90; modal.FeedMode = 94; modal.Coordinate = 54;
                    modal.Spindle = actualSpindle = 5; modal.Coolant = actualCoolant = 0; modal.Program = 0;
                    feedOverride = rapidOverride = spindleOverride = 100;
                    Emit("[MSG:Pgm End]");
                }
                CompleteAck(); return;
            }
            var speed = block.Rapid ? AxisRate(block) * rapidOverride / 100.0 : Math.Min(block.Feed * (block.Probe ? 1 : feedOverride / 100.0), AxisRate(block));
            double spent, nextProgress;
            if (block.Dwell > 0)
            {
                currentFeed = velocity = 0;
                spent = Math.Min(seconds, block.Dwell * (1 - block.Progress));
                nextProgress = Math.Min(1, block.Progress + spent / block.Dwell);
            }
            else if (block.PathLength == 0) { spent=0; nextProgress=1; }
            else
            {
                // Integrate in small deterministic slices. Axis acceleration is in mm/s² ($120..122).
                spent = Math.Min(seconds, 0.005);
                var acceleration = AxisAcceleration(block);
                var exit = JunctionSpeed(block, speed / 60, acceleration);
                var remainingDistance = block.PathLength * (1 - block.Progress);
                var desired = Math.Min(speed / 60, Math.Sqrt(exit * exit + 2 * acceleration * remainingDistance));
                var newVelocity = velocity < desired ? Math.Min(desired, velocity + acceleration * spent) : Math.Max(desired, velocity - acceleration * spent);
                var distance = (velocity + newVelocity) * .5 * spent;
                if (remainingDistance < Math.Max(1e-8, newVelocity * 0.005) && desired < velocity) distance = remainingDistance;
                velocity = newVelocity; currentFeed = velocity * 60;
                nextProgress = Math.Min(1, block.Progress + distance / block.PathLength);
            }
            var next = nextProgress == 1 ? block.End : block.At(nextProgress);
            if (block.Probe && ProbeAt(next) == !block.ProbeAway)
            {
                if(!probePin)
                {
                    var start=position;var end=next;double low=0,high=1;
                    for(int i=0;i<40;i++){var middle=(low+high)/2;if(ProbeAt(start+(end-start)*middle)==!block.ProbeAway)high=middle;else low=middle;}
                    next=start+(end-start)*high;
                }
                var segmentStart=position; position = Quantize(next); MotionAdvanced?.Invoke(new(segmentStart,position,block.Rapid)); PositionChanged?.Invoke(position);
                if(UpdateTravelLimits()) return;
                probePosition = position; probeSuccess = true; modal.Position = position;
                planner.Dequeue(); EmitProbe(); state = "Idle"; currentFeed = 0; CompleteAck(); return;
            }
            block.Progress = nextProgress; var motionStart=position; position = Quantize(next); MotionAdvanced?.Invoke(new(motionStart,position,block.Rapid)); PositionChanged?.Invoke(position); seconds -= spent;
            if(UpdateTravelLimits()) return;
            if (nextProgress < 1) { if (seconds > 1e-9) continue; return; }
            planner.Dequeue();
            if (block.Probe)
            {
                probePosition = position; modal.Position = position; EmitProbe();
                if (!block.ProbeNoError) Alarm(5);
                else state = "Idle";
                currentFeed = velocity = 0; CompleteAck(true); return;
            }
            if (planner.Count == 0) { state = "Idle"; currentFeed = velocity = 0; CompleteAck(); }
        }
    }
    private bool ProbeAt(Vec p) => probePin || (ProbeSurface(p) is {} surface && p.Z <= surface);
    private Vec Quantize(Vec p)
    {
        for (int i = 0; i < 3; i++) if (Setting(100 + i) > 0) p = p.With(i, Math.Round(p[i] * Setting(100 + i), MidpointRounding.AwayFromZero) / Setting(100 + i));
        return p;
    }
    private double AxisRate(MotionBlock block)
    {
        double rate = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            double fraction = block.Radius > 0 && i != block.LinearAxis ? 1 : Math.Abs(block.End[i] - block.Start[i]) / Math.Max(block.PathLength, 1e-9);
            if (fraction > 1e-9) rate = Math.Min(rate, Setting(110 + i) / fraction);
        }
        return rate == double.MaxValue ? 500 : rate;
    }
    private double AxisAcceleration(MotionBlock block)
    {
        double acceleration = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var fraction = block.Radius > 0 && i != block.LinearAxis ? 1 : Math.Abs(block.End[i] - block.Start[i]) / Math.Max(block.PathLength, 1e-9);
            if (fraction > 1e-9) acceleration = Math.Min(acceleration, Setting(120 + i) / fraction);
        }
        return acceleration == double.MaxValue ? 10 : Math.Max(0, acceleration);
    }
    private double JunctionSpeed(MotionBlock block, double nominal, double acceleration)
    {
        if (planner.Count < 2 || block.Probe || block.Action is not null) return 0;
        var next = planner.ElementAt(1);
        if (next.Probe || next.Action is not null || next.PathLength == 0) return 0;
        var a = block.Tangent(1); var b = next.Tangent(0);
        var dot = Math.Clamp(a.X*b.X + a.Y*b.Y + a.Z*b.Z, -1, 1);
        var nextSpeed = (next.Rapid ? AxisRate(next)*rapidOverride/100 : Math.Min(next.Feed*feedOverride/100,AxisRate(next))) / 60;
        var limit = Math.Min(nominal,nextSpeed);
        if (dot > .999999) return Math.Min(limit,Math.Sqrt(2*AxisAcceleration(next)*next.PathLength));
        if (dot < -.999999) return 0;
        var sinHalf = Math.Sqrt((1+dot)*.5);
        return Math.Min(limit,Math.Sqrt(acceleration*Setting(11)*sinHalf/(1-sinHalf)));
    }
    private void CompleteAck(bool force = false)
    {
        if (deferredAck || force) { deferredAck = false; Reply(); }
    }
}
