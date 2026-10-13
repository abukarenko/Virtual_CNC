using System.Globalization;
using System.Text.Json;

namespace VirtualCnc.Core;

public readonly record struct Vec(double X, double Y, double Z)
{
    public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
    public Vec With(int i, double v) => i == 0 ? this with { X = v } : i == 1 ? this with { Y = v } : this with { Z = v };
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec operator *(Vec a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public string Format(bool inches = false)
    {
        var vector = this;
        return string.Join(',', Enumerable.Range(0, 3).Select(i => (vector[i] / (inches ? 25.4 : 1)).ToString(inches ? "F4" : "F3", CultureInfo.InvariantCulture)));
    }
}

public sealed class Eeprom
{
    public SortedDictionary<int, double> Settings { get; set; } = Defaults();
    public Vec[] Coordinates { get; set; } = new Vec[8];
    public string[] Startup { get; set; } = ["", ""];
    public string BuildInfo { get; set; } = "";
    public bool TableWorkOriginInitialized { get; set; }
    public double MaterialThickness { get; set; } = 20;
    public bool MaterialProbeEnabled { get; set; } = true;
    public SurfaceProfile Surface {get;set;}=new(){Coordinates=SurfaceCoordinates.Work};
    public static SortedDictionary<int, double> Defaults() => new()
    {
        [0]=10,[1]=25,[2]=0,[3]=0,[4]=0,[5]=0,[6]=0,
        [10]=1,[11]=0.01,[12]=0.002,[13]=0,[20]=0,[21]=0,[22]=0,[23]=0,
        [24]=25,[25]=500,[26]=250,[27]=1,[30]=1000,[31]=0,[32]=0,
        [100]=250,[101]=250,[102]=250,[110]=500,[111]=500,[112]=500,
        [120]=10,[121]=10,[122]=10,[130]=200,[131]=200,[132]=200
    };
}

public sealed class MemoryStore(string? path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string? LoadWarning { get; private set; }
    public Eeprom Load()
    {
        if (path is null || !File.Exists(path)) return new();
        try
        {
            var memory = JsonSerializer.Deserialize<Eeprom>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty EEPROM");
            if (memory.Coordinates.Length != 8 || memory.Startup.Length != 2 || memory.Startup.Any(s => s is null || s.Length > 79)) throw new InvalidDataException("Invalid EEPROM dimensions");
            foreach (var pair in Eeprom.Defaults())
                if (!memory.Settings.ContainsKey(pair.Key)) memory.Settings[pair.Key] = pair.Value;
            if (memory.Settings.Values.Any(v => !double.IsFinite(v) || v < 0) || memory.Coordinates.Any(v => !double.IsFinite(v.X + v.Y + v.Z))) throw new InvalidDataException("Invalid EEPROM values");
            return memory;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            LoadWarning = ex.Message;
            // Never overwrite a damaged memory file without preserving it.
            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            return new();
        }
    }
    public void Save(Eeprom memory)
    {
        if (path is null) return;
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temporary = absolute + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(memory, JsonOptions));
        File.Move(temporary, absolute, true);
    }
}

public sealed record MachineSnapshot(string State, Vec MachinePosition, Vec WorkPosition, Vec PlannedPosition,
    double Feed, double Spindle, int FeedOverride, int RapidOverride, int SpindleOverride, int Buffered,
    bool Probe, string Limits, bool Door, int SpindleMode, int Coolant, string Parser, Vec MaxTravel = default, double MaterialThickness = 0, bool MaterialProbeEnabled = false, double SurfaceDelta = 0, Vec? PhysicalPosition = null);

internal sealed class Modal
{
    public double Motion = 0;
    public int Plane = 17, Units = 21, Distance = 90, FeedMode = 94, Coordinate = 54, ToolLengthMode = 49;
    public int Spindle = 5, Coolant = 0, Tool, Program;
    public double Feed, Speed, ToolLength;
    public Vec Position, Offset;
    public Modal Copy() => (Modal)MemberwiseClone();
    public string Report(bool inches) => $"[GC:G{Motion.ToString("0.0#", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')} G{Coordinate} G{Plane} G{Units} G{Distance} G{FeedMode}" + (Program == 0 ? "" : $" M{(Program == 3 ? 0 : Program)}") + $" M{Spindle} " +
        (Coolant == 0 ? "M9" : string.Join(' ', new[] { Coolant.HasFlag(2) ? "M7" : "", Coolant.HasFlag(1) ? "M8" : "" }.Where(s=>s.Length>0))) +
        $" T{Tool} F{(Feed / (inches ? 25.4 : 1)).ToString(inches ? "F1" : "F0", CultureInfo.InvariantCulture)} S{Speed.ToString("F0", CultureInfo.InvariantCulture)}]";
}

internal static class BitExtensions
{
    public static bool HasFlag(this int value, int flag) => (value & flag) != 0;
}

internal sealed class MotionBlock
{
    public Vec Start, End, Center;
    public int Axis0, Axis1, LinearAxis, Line, SpindleMode = 5, Coolant;
    public double Sweep, Radius, Feed, Speed, Dwell, Progress, PathLength;
    public bool Rapid, Jog, Probe, ProbeAway, ProbeNoError, Started;
    public string? Action;
    public Vec At(double t)
    {
        if (Radius == 0) return Start + (End - Start) * t;
        var angle = Math.Atan2(Start[Axis1] - Center[Axis1], Start[Axis0] - Center[Axis0]) + Sweep * t;
        return (Start + (End - Start) * t).With(Axis0, Center[Axis0] + Radius * Math.Cos(angle)).With(Axis1, Center[Axis1] + Radius * Math.Sin(angle));
    }
    public Vec Tangent(double t)
    {
        if (PathLength == 0) return default;
        if (Radius == 0) return (End - Start) * (1 / PathLength);
        var angle = Math.Atan2(Start[Axis1] - Center[Axis1], Start[Axis0] - Center[Axis0]) + Sweep * t;
        return new Vec().With(Axis0, -Radius * Sweep * Math.Sin(angle) / PathLength)
            .With(Axis1, Radius * Sweep * Math.Cos(angle) / PathLength).With(LinearAxis, (End[LinearAxis] - Start[LinearAxis]) / PathLength);
    }
}

public readonly record struct MotionSegment(Vec Start, Vec End, bool Rapid);
