using System.Globalization;
using System.Text.RegularExpressions;

namespace VirtualCnc.Core;

internal sealed class GrblError(int code) : Exception
{
    public int Code { get; } = code;
}

public sealed partial class GrblController
{
    private static readonly Regex Number = new(@"\G[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static void Require(bool condition, int error) { if (!condition) throw new GrblError(error); }
    private sealed record Parsed(Modal Modal, List<MotionBlock> Blocks, List<Action> Writes, bool Sync);

    private Parsed Parse(string line, bool jog = false)
    {
        var m = modal.Copy();
        if (jog) { m.Motion = 1; m.FeedMode = 94; }
        var words = new Dictionary<char, double>();
        var groups = new HashSet<int>();
        var commands = new List<(char Letter, double Code)>();
        double nonModal = -1;
        int axisCommand = 0; // 1 motion, 2 non-modal axis, 3 tool length
        void AxisCommand(int type) { Require(axisCommand == 0, 24); axisCommand = type; }
        for (var p = 0; p < line.Length;)
        {
            var letter = line[p++];
            Require(letter is >= 'A' and <= 'Z', 1);
            var match = Number.Match(line, p);
            Require(match.Success, 2);
            var value = double.Parse(match.Value, CultureInfo.InvariantCulture);
            Require(double.IsFinite(value), 2);
            p += match.Length;
            if (letter is 'G' or 'M')
            {
                Require(value >= 0, 20);
                var integer = (int)Math.Truncate(value);
                var fraction = (int)Math.Round((value - integer) * 100, MidpointRounding.AwayFromZero);
                var code = integer + fraction / 100.0;
                int group;
                if (letter == 'G')
                {
                    switch (integer)
                    {
                        case 4: case 10: case 28: case 30: case 53: case 92:
                            group = 0;
                            if (integer is 28 or 30 or 92) Require(fraction is 0 or 10, 20);
                            else Require(fraction == 0, 23);
                            if (integer is 10 or 28 or 30 or 92 && fraction == 0) AxisCommand(2);
                            nonModal = code;
                            break;
                        case 0: case 1: case 2: case 3: case 38: case 80:
                            group = 1;
                            if (integer == 38) Require(fraction is 20 or 30 or 40 or 50, 20);
                            else Require(fraction == 0, 23);
                            if (integer != 80) AxisCommand(1);
                            m.Motion = code;
                            break;
                        case 17: case 18: case 19:
                            Require(fraction == 0, 23); group = 2; m.Plane = integer; break;
                        case 90: case 91:
                            if (fraction == 0) { group = 3; m.Distance = integer; }
                            else { Require(integer == 91 && fraction == 10, 20); group = 4; }
                            break;
                        case 93: case 94:
                            Require(fraction == 0, 23); group = 5; m.FeedMode = integer; break;
                        case 20: case 21:
                            Require(fraction == 0, 23); group = 6; m.Units = integer; break;
                        case 40: Require(fraction == 0, 23); group = 7; break;
                        case 43: case 49:
                            Require(integer == 49 ? fraction == 0 : fraction == 10, integer == 49 ? 23 : 20);
                            group = 8; AxisCommand(3); m.ToolLengthMode = integer == 49 ? 49 : 431; break;
                        case >= 54 and <= 59:
                            Require(fraction == 0, 23); group = 9; m.Coordinate = integer; break;
                        case 61: Require(fraction == 0, 20); group = 10; break;
                        default: throw new GrblError(20);
                    }
                }
                else
                {
                    Require(fraction == 0, 23);
                    switch (integer)
                    {
                        case 0: case 1: case 2: case 30:
                            group = 11; if (integer != 1) m.Program = integer == 0 ? 3 : integer; break;
                        case 3: case 4: case 5: group = 12; m.Spindle = integer; break;
                        case 7:
                            Require(EnableMist, 20); group = 13; m.Coolant |= 2; break;
                        case 8: group = 13; m.Coolant |= 1; break;
                        case 9: group = 13; m.Coolant = 0; break;
                        // M56 requires an optional firmware build. Default 1.1f rejects it.
                        default: throw new GrblError(20);
                    }
                }
                Require(groups.Add(group), 21);
                commands.Add((letter, code));
            }
            else
            {
                Require("FIJKLNPRSTXYZ".Contains(letter), 20);
                Require(words.TryAdd(letter, value), 25);
                if ("FNPTS".Contains(letter)) Require(value >= 0, 4);
                if (letter == 'T') Require(value <= 255, 38);
            }
        }
        var axes = "XYZ".Where(words.ContainsKey).ToArray();
        var unused = words.Keys.ToHashSet();
        void Use(params char[] letters) { foreach (var letter in letters) unused.Remove(letter); }
        double Word(char c, double fallback = 0) => words.GetValueOrDefault(c, fallback);
        bool Has(char c) => words.ContainsKey(c);
        if (axes.Length != 0 && axisCommand == 0) axisCommand = 1;
        if (Has('N')) Require(Word('N') <= 10000000, 27);
        var factor = m.Units == 20 ? 25.4 : 1;
        if (jog) Require(Has('F'), 22);
        if (m.FeedMode == 93)
        {
            if (axisCommand == 1 && m.Motion is not (0 or 80)) Require(Has('F'), 22);
            m.Feed = Word('F');
        }
        else m.Feed = Has('F') ? Word('F') * factor : modal.FeedMode == 94 ? modal.Feed : 0;
        if (Has('S')) m.Speed = Word('S');
        if (Has('T')) m.Tool = (int)Word('T');
        Use('F', 'N', 'S', 'T');
        if (nonModal == 4) { Require(Has('P'), 28); Use('P'); }
        if (axisCommand == 3 && m.ToolLengthMode == 431) Require(axes.Length == 1 && axes[0] == 'Z', 37);
        if (m.Motion == 80) Require(axes.Length == 0, 31);
        var coordinate = Memory.Coordinates[m.Coordinate - 54];
        var target = m.Position;
        var values = new Vec(Word('X') * factor, Word('Y') * factor, Word('Z') * factor);
        for (int i = 0; i < 3; i++)
        {
            if (!Has("XYZ"[i])) continue;
            var v = values[i];
            if (nonModal != 53) v += m.Distance == 91 ? m.Position[i] : coordinate[i] + m.Offset[i] + (i == 2 ? m.ToolLength : 0);
            target = target.With(i, v);
        }
        var writes = new List<Action>();
        var blocks = new List<MotionBlock>();
        bool sync = groups.Overlaps([8, 9, 12, 13]) || nonModal is 4 or 10 or 28 or 30 or 28.1 or 30.1 or 92 or 92.1 || groups.Contains(11);
        if (nonModal == 10)
        {
            Require(axes.Length > 0, 26); Require(Has('P') && Has('L'), 28);
            int index = (int)Word('P'); Require(index <= 6, 29);
            Require((int)Word('L') is 2 or 20, 20);
            if ((int)Word('L') == 2) Require(!Has('R'), 20);
            index = index == 0 ? m.Coordinate - 54 : index - 1;
            var updated = Memory.Coordinates[index];
            for (int i = 0; i < 3; i++) if (Has("XYZ"[i]))
                updated = updated.With(i, (int)Word('L') == 2 ? values[i] : m.Position[i] - m.Offset[i] - (i == 2 ? m.ToolLength : 0) - values[i]);
            var data = updated;
            writes.Add(() => Memory.Coordinates[index] = data);
            Use('P', 'L');
        }
        else if (nonModal == 92)
        {
            Require(axes.Length > 0, 26);
            for (int i = 0; i < 3; i++) if (Has("XYZ"[i])) m.Offset = m.Offset.With(i, m.Position[i] - coordinate[i] - (i == 2 ? m.ToolLength : 0) - values[i]);
        }
        else if (nonModal == 92.1) m.Offset = default;
        else if (nonModal is 28.1 or 30.1)
        {
            var index = nonModal == 28.1 ? 6 : 7;
            var position = m.Position;
            writes.Add(() => Memory.Coordinates[index] = position);
        }
        if (axisCommand == 3) m.ToolLength = m.ToolLengthMode == 49 ? 0 : values.Z;
        if (nonModal == 53) Require(m.Motion is 0 or 1, 30);

        MotionBlock Linear(Vec from, Vec to, bool rapid = false) => new()
        {
            Start = from, End = to, PathLength = (to - from).Length, Rapid = rapid,
            Feed = m.FeedMode == 93 ? (to - from).Length * m.Feed : m.Feed,
            Jog = jog, Line = (int)Word('N'), SpindleMode = m.Spindle, Speed = m.Speed, Coolant = m.Coolant
        };
        if (nonModal is 28 or 30)
        {
            if (axes.Length > 0) blocks.Add(Linear(m.Position, target, true));
            var home = Memory.Coordinates[nonModal == 28 ? 6 : 7];
            if (axes.Length > 0) for (int i = 0; i < 3; i++) if (!Has("XYZ"[i])) home = home.With(i, m.Position[i]);
            blocks.Add(Linear(axes.Length > 0 ? target : m.Position, home, true));
            m.Position = home;
        }
        else if (axisCommand == 1 && m.Motion != 80)
        {
            if (m.Motion != 0) Require(m.Feed > 0, 22);
            if (m.Motion is 2 or 3)
            {
                Require(axes.Length > 0, 26);
                int a = m.Plane == 17 ? 0 : m.Plane == 18 ? 2 : 1;
                int b = m.Plane == 17 ? 1 : m.Plane == 18 ? 0 : 2;
                int c = 3 - a - b;
                Require(Has("XYZ"[a]) || Has("XYZ"[b]), 32);
                var delta = target - m.Position;
                double i, j;
                if (Has('R'))
                {
                    Require(delta.Length > 1e-10, 33);
                    var r = Word('R') * factor;
                    var squared = 4 * r * r - delta[a] * delta[a] - delta[b] * delta[b];
                    Require(squared >= 0, 34);
                    var chord = Math.Sqrt(delta[a] * delta[a] + delta[b] * delta[b]);
                    Require(chord > 0, 34);
                    var h = -Math.Sqrt(squared) / chord;
                    if (m.Motion == 3) h = -h;
                    if (r < 0) h = -h;
                    i = 0.5 * (delta[a] - delta[b] * h);
                    j = 0.5 * (delta[b] + delta[a] * h);
                    Use('R');
                }
                else
                {
                    Require(Has("IJK"[a]) || Has("IJK"[b]), 35);
                    i = Word("IJK"[a]) * factor; j = Word("IJK"[b]) * factor;
                    var radius0 = Math.Sqrt(i * i + j * j);
                    var radius1 = Math.Sqrt(Math.Pow(delta[a] - i, 2) + Math.Pow(delta[b] - j, 2));
                    var difference = Math.Abs(radius0 - radius1);
                    Require(difference <= 0.005 || (difference <= 0.5 && difference <= 0.001 * radius0), 33);
                    Use('I', 'J', 'K');
                }
                var center = m.Position.With(a, m.Position[a] + i).With(b, m.Position[b] + j);
                var start = Math.Atan2(-j, -i);
                var end = Math.Atan2(target[b] - center[b], target[a] - center[a]);
                var sweep = Math.Atan2(Math.Sin(end - start), Math.Cos(end - start));
                if (m.Motion == 2 && sweep >= -1e-7) sweep -= 2 * Math.PI;
                if (m.Motion == 3 && sweep <= 1e-7) sweep += 2 * Math.PI;
                var radius = Math.Sqrt(i * i + j * j);
                Require(radius > 0, 33);
                var length = Math.Sqrt(Math.Pow(radius * sweep, 2) + Math.Pow(delta[c], 2));
                blocks.Add(new MotionBlock { Start=m.Position, End=target, Center=center, Radius=radius, Sweep=sweep, Axis0=a, Axis1=b, LinearAxis=c, PathLength=length,
                    Feed=m.FeedMode == 93 ? length * m.Feed : m.Feed, SpindleMode=m.Spindle, Speed=m.Speed, Coolant=m.Coolant, Line=(int)Word('N') });
                m.Position = target;
            }
            else if (m.Motion >= 38)
            {
                Require(axes.Length > 0, 26); Require((target - m.Position).Length > 1e-10, 33);
                var block = Linear(m.Position, target);
                block.Probe = true; block.ProbeAway = m.Motion is 38.4 or 38.5; block.ProbeNoError = m.Motion is 38.3 or 38.5;
                blocks.Add(block); m.Position = target; sync = true;
            }
            else if (axes.Length > 0) { blocks.Add(Linear(m.Position, target, m.Motion == 0)); m.Position = target; }
        }
        if (axisCommand != 0) Use('X', 'Y', 'Z');
        Require(unused.Count == 0, 36);
        if (jog)
        {
            Require(commands.All(cmd => cmd.Letter == 'G' && cmd.Code is 20 or 21 or 90 or 91 or 53), 16);
            Require(words.Keys.All(c => "XYZF".Contains(c)), 16);
        }
        if (nonModal == 4) blocks.Insert(0, new MotionBlock { Dwell=Word('P'), Start=modal.Position, End=modal.Position, Action="dwell" });
        if (groups.Contains(11) && commands.Any(c => c.Letter == 'M' && c.Code is 0 or 2 or 30))
            blocks.Add(new MotionBlock { Start=m.Position, End=m.Position, Action=m.Program == 3 ? "pause" : "end" });
        return new Parsed(m, blocks, writes, sync);
    }
}
