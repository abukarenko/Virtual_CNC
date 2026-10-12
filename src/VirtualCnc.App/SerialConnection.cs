using System.IO.Ports;
using System.Threading.Channels;
using VirtualCnc.Core;

namespace VirtualCnc.App;

internal sealed class SerialConnection(GrblController engine) : IDisposable
{
    private SerialPort? serial;
    private Channel<string>? outgoing;
    private Task? writer;
    private CancellationTokenSource? lifetime;
    private readonly object traceGate = new();
    private readonly string tracePath = Path.Combine(AppContext.BaseDirectory, "data", "serial.log");
    public bool IsOpen => serial?.IsOpen == true;
    public event Action<string>? Received;
    public event Action<string>? Fault;
    public void Open(string port, int baudRate=115200)
    {
        Close();
        var connection = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One)
        { Handshake=Handshake.None, DtrEnable=false, RtsEnable=false, ReadTimeout=500, WriteTimeout=1000 };
        connection.Open();
        Trace($"OPEN {port} {baudRate} 8N1");
        serial = connection;
        outgoing = Channel.CreateUnbounded<string>(new() { SingleReader=true });
        var queue = outgoing;
        lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in queue.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        // Async writes remain pending until a peer is ready. A disconnected
                        // peer must not kill the only output worker on the initial banner.
                        await connection.BaseStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(line + "\r\n"), token);
                        Trace("TX " + line);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                    {
                        if (token.IsCancellationRequested) break;
                        Trace("WRITE ERROR " + ex.Message); Fault?.Invoke(ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        engine.Output += Send;
        connection.DataReceived += OnData;
        connection.PinChanged += OnPin;
        engine.Boot();
    }
    private void OnPin(object sender, SerialPinChangedEventArgs e)
    {
        // com0com maps the peer's DTR to DSR: opening an Arduino-style sender resets GRBL.
        try { if (e.EventType == SerialPinChange.DsrChanged && ((SerialPort)sender).DsrHolding) engine.Receive(new byte[] {0x18}); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Fault?.Invoke(ex.Message); }
    }
    private void Send(string line) => outgoing?.Writer.TryWrite(line);
    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var connection = (SerialPort)sender;
            while (connection.IsOpen && connection.BytesToRead > 0)
            {
                var bytes = new byte[Math.Min(256, connection.BytesToRead)];
                int length = connection.Read(bytes, 0, bytes.Length);
                var text=Format(bytes.AsSpan(0, length));
                Trace("RX " + text); Received?.Invoke(text);
                engine.Receive(bytes.AsSpan(0, length));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { Trace("READ ERROR " + ex.Message); Fault?.Invoke(ex.Message); }
    }
    private static string Format(ReadOnlySpan<byte> bytes)
    {
        var result = new System.Text.StringBuilder();
        foreach (var b in bytes) result.Append(b is >= 32 and < 127 ? ((char)b).ToString() : b is 10 or 13 ? " " : $"<{b:X2}>");
        return result.ToString();
    }
    public void Close()
    {
        engine.Output -= Send;
        outgoing?.Writer.TryComplete();
        lifetime?.Cancel();
        if (serial is { } connection)
        {
            connection.DataReceived -= OnData;
            connection.PinChanged -= OnPin;
            connection.Close(); connection.Dispose(); serial = null;
        }
        outgoing = null;
        if (writer is { } task) { try { task.Wait(1500); } catch (AggregateException) { } writer = null; }
        lifetime?.Dispose(); lifetime=null;
    }
    private void Trace(string text)
    {
        try
        {
            lock(traceGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
                if(File.Exists(tracePath) && new FileInfo(tracePath).Length>2000000) File.Move(tracePath,tracePath+".previous",true);
                File.AppendAllText(tracePath,DateTimeOffset.Now.ToString("O")+" "+text+Environment.NewLine);
            }
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }
    public void Dispose() => Close();
}
