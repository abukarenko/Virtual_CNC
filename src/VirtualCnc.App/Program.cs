using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VirtualCnc.Core;

namespace VirtualCnc.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var consoleMode = args.Any(arg => arg is "--help" or "--list-ports" or "--port" or "--tcp" or "--stdio");
        if (consoleMode && GetFileType(GetStdHandle(-11)) is not (1 or 3) && AttachConsole(uint.MaxValue))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush=true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush=true });
        }
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        if (args.Contains("--help"))
        {
            Console.WriteLine("Virtual CNC / GRBL 1.1f\n  (no arguments)          desktop simulator\n  --port COM20           serial endpoint, 115200 8N1\n  --stdio                raw byte protocol over standard input/output\n  --tcp 7000             loopback TCP endpoint\n  --memory <file>        persistent EEPROM file\n  --speed <multiplier>   simulation speed, default 1\n  --mist                 enable optional M7 support\n  --list-ports           list installed COM ports");
            return 0;
        }
        if (args.Contains("--list-ports")) { foreach (var port in System.IO.Ports.SerialPort.GetPortNames()) Console.WriteLine(port); return 0; }
        string? Option(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}");
            return args[index + 1];
        }
        try
        {
            var memory = Option("--memory") ?? Path.Combine(AppContext.BaseDirectory, "data", "eeprom.json");
            var engine = new GrblController(memory, args.Contains("--mist"));
            if (Option("--speed") is { } speed) engine.TimeScale = double.Parse(speed, CultureInfo.InvariantCulture);
            if (engine.MemoryWarning is { } warning) Console.Error.WriteLine("EEPROM restored from defaults: " + warning);
            if(!args.Contains("--render-ui")) engine.InitializeTableWorkOrigin();
            if (args.Contains("--stdio")) return StreamMode(engine, Console.OpenStandardInput(), Console.OpenStandardOutput()).GetAwaiter().GetResult();
            if (Option("--tcp") is { } tcp) return TcpMode(engine, int.Parse(tcp, CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
            if (Option("--port") is { } port) return SerialMode(engine, port).GetAwaiter().GetResult();
            ApplicationConfiguration.Initialize();
            if (Option("--render-ui") is { } imagePath)
            {
                using var preview = new MainForm(engine);
                preview.StartPosition = FormStartPosition.Manual;
                preview.Location = new Point(Screen.AllScreens.Max(s => s.Bounds.Right) + 100, 100);
                preview.Show(); Application.DoEvents();
                if(Option("--render-size") is { } size) {var dimensions=size.Split('x');preview.Size=new Size(int.Parse(dimensions[0],CultureInfo.InvariantCulture),int.Parse(dimensions[1],CultureInfo.InvariantCulture));Application.DoEvents();}
                if(Option("--render-tab") is { } tab) {var tabs=preview.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();tabs.SelectedIndex=int.Parse(tab,CultureInfo.InvariantCulture);Application.DoEvents();}
                if(args.Contains("--render-demo"))
                {
                    engine.InitializeTableWorkOrigin();engine.Receive("$X\n");engine.Receive("G0X5Y5\n");
                    for(int i=0;i<2000 && engine.Snapshot().State=="Run";i++) engine.Tick(TimeSpan.FromSeconds(.1));
                    engine.Receive("G1X120Y5F1800\nG1X120Y150\nG1X5Y150\nG1X5Y5\nG0X60Y70\n");
                    for(int i=0;i<2000 && engine.Snapshot().State=="Run";i++) engine.Tick(TimeSpan.FromSeconds(.1));
                }
                preview.RefreshPreview();
                using var bitmap = new Bitmap(preview.Width, preview.Height);
                preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
                bitmap.Save(Path.GetFullPath(imagePath), System.Drawing.Imaging.ImageFormat.Png);
                preview.Close();
                return 0;
            }
            Application.Run(new MainForm(engine, Option("--connect")));
            return 0;
        }
        catch (Exception ex)
        {
            if (consoleMode) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "Virtual CNC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
    internal static async Task Ticker(GrblController engine, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(token))
        {
            var now = clock.Elapsed;
            engine.Tick(now - previous); previous = now;
        }
    }
    private static async Task<int> StreamMode(GrblController engine, Stream input, Stream output)
    {
        using var cancellation = new CancellationTokenSource();
        var queue = System.Threading.Channels.Channel.CreateUnbounded<string>(new() { SingleReader=true });
        void Emit(string line) => queue.Writer.TryWrite(line + "\r\n");
        engine.Output += Emit;
        var writer = Task.Run(async () =>
        {
            await foreach (var line in queue.Reader.ReadAllAsync())
            {
                await output.WriteAsync(System.Text.Encoding.ASCII.GetBytes(line)); await output.FlushAsync();
            }
        });
        var ticker = Ticker(engine, cancellation.Token);
        engine.Boot();
        try
        {
            var buffer = new byte[256];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0) engine.Receive(buffer.AsSpan(0, count));
        }
        finally
        {
            cancellation.Cancel();
            try { await ticker; } catch (OperationCanceledException) { }
            engine.Output -= Emit; queue.Writer.TryComplete(); await writer;
        }
        return 0;
    }
    private static async Task<int> TcpMode(GrblController engine, int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine($"Virtual CNC listening on 127.0.0.1:{port}. Ctrl+C to close.");
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                try { await StreamMode(engine, client.GetStream(), client.GetStream()); }
                catch (IOException ex) { Console.Error.WriteLine(ex.Message); }
            }
        }
        finally { listener.Stop(); }
    }
    private static async Task<int> SerialMode(GrblController engine, string port)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        using var serial = new SerialConnection(engine);
        serial.Open(port);
        Console.Error.WriteLine($"Virtual CNC connected to {port}, 115200 8N1. Ctrl+C to close.");
        try { await Ticker(engine, cancellation.Token); } catch (OperationCanceledException) { }
        return 0;
    }
}
