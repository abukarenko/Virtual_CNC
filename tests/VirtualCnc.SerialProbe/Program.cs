using System.IO.Ports;
using System.Diagnostics;

var name = args.Length > 0 ? args[0] : "COM15";
try
{
    using var port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
    { Handshake=Handshake.None, ReadTimeout=200, WriteTimeout=2000, DtrEnable=false, RtsEnable=false };
    port.Open();
    Console.WriteLine($"Opened {name} at 115200 8N1, no flow control.");
    port.Write("$$\n");
    var clock = Stopwatch.StartNew();
    bool settings=false;
    while (clock.Elapsed.TotalSeconds < 4)
    {
        try
        {
            var line=port.ReadLine().TrimEnd('\r'); Console.WriteLine("RX " + line);
            if(line.StartsWith("$100=")) settings=true;
            if(line=="ok" && settings) break;
        }
        catch (TimeoutException) { }
    }
    port.Write("?"); clock.Restart(); bool status=false;
    while(clock.Elapsed.TotalSeconds < 3)
    {
        try { var line=port.ReadLine().TrimEnd('\r'); Console.WriteLine("RX " + line); if(line.StartsWith('<')) { status=true; break; } }
        catch(TimeoutException) { }
    }
    Console.WriteLine(settings && status ? "PASS: settings and realtime status received over COM." : $"FAIL: settings={settings}, status={status}");
    return settings && status ? 0 : 1;
}
catch(UnauthorizedAccessException ex) { Console.WriteLine("BUSY: port is already open in another application. " + ex.Message); return 2; }
catch(Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
