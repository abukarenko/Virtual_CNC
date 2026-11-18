using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using VirtualCnc.Core;

namespace VirtualCnc.App;

// GUI transport: the form continues ticking the engine while this server handles bytes.
internal sealed class TcpConnection(GrblController engine) : IDisposable
{
    private TcpListener? listener;
    private CancellationTokenSource? lifetime;
    private TcpClient? client;
    private readonly object gate = new();
    public bool IsOpen => listener is not null;
    public event Action<string>? Received;
    public event Action<string>? Fault;
    public event Action<string>? Status;

    public void Open(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address)) throw new ArgumentException("Укажите локальный IP-адрес, например 127.0.0.1 или 0.0.0.0.");
        if (port is < 1 or > 65535) throw new ArgumentException("TCP-порт: 1–65535.");
        Close();
        var server = new TcpListener(address, port);
        server.Start();
        listener = server;
        lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        _ = Accept(server, token);
        Status?.Invoke($"TCP {host}:{port} · ожидание клиента");
    }

    private async Task Accept(TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var incoming = await server.AcceptTcpClientAsync(token);
                incoming.NoDelay = true;
                lock (gate)
                {
                    if (client is not null) { incoming.Dispose(); continue; }
                    client = incoming;
                }
                _ = Session(incoming, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        { if (!token.IsCancellationRequested) Fault?.Invoke(ex.Message); }
    }

    private async Task Session(TcpClient connection, CancellationToken serverToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var token = cancellation.Token;
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { SingleReader=true, FullMode=BoundedChannelFullMode.Wait });
        void Emit(string line)
        {
            if (!queue.Writer.TryWrite(line)) { Fault?.Invoke("Переполнение TCP-очереди. Соединение закрыто."); cancellation.Cancel(); connection.Dispose(); }
        }
        engine.Output += Emit;
        Emit(GrblController.Banner);
        Status?.Invoke("TCP · клиент подключён");
        async Task Write()
        {
            try
            {
                await foreach (var line in queue.Reader.ReadAllAsync(token))
                    await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes(line+"\r\n"),token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            { if (!token.IsCancellationRequested) Fault?.Invoke(ex.Message); cancellation.Cancel(); connection.Dispose(); }
        }
        var writer = Write();
        try
        {
            var bytes = new byte[256];
            int count;
            while ((count = await connection.GetStream().ReadAsync(bytes,token)) > 0)
            {
                Received?.Invoke(string.Concat(bytes.Take(count).Select(b=>b is >=32 and <127 ? ((char)b).ToString() : $"<{b:X2}>")));
                engine.Receive(bytes.AsSpan(0,count));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { if (!token.IsCancellationRequested) Fault?.Invoke(ex.Message); }
        finally
        {
            engine.Output -= Emit;
            cancellation.Cancel(); connection.Dispose(); queue.Writer.TryComplete();
            await writer;
            // Never let a lost GUI connection silently continue queued movement.
            if (engine.Snapshot().State is "Run" or "Jog") engine.Receive("!");
            lock (gate) { if (ReferenceEquals(client,connection)) client=null; }
            if (!serverToken.IsCancellationRequested) Status?.Invoke("TCP · клиент отключён, ожидание подключения");
        }
    }

    public void Close()
    {
        lifetime?.Cancel(); listener?.Stop(); listener=null;
        lock (gate) { client?.Dispose(); }
        lifetime?.Dispose(); lifetime=null;
    }
    public void Dispose() => Close();
}
