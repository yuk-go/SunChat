using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SunChat;

// Discovery (UDP broadcast) + messaging/file transfer (TCP). All events fire on
// background threads; the UI marshals them onto the dispatcher.
public class NetworkService : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string DisplayName { get; set; }
    public string Group { get; set; } = "기본";
    public string DownloadDir { get; set; }   // where received files are saved; set from Settings
    public int RequestedPort { get; set; } = 51001; // preferred fixed listener port; set from Settings
    public int TcpPort { get; private set; }

    private UdpClient _udp;
    private TcpListener _tcp;
    private System.Threading.Timer _broadcastTimer;
    private readonly CancellationTokenSource _cts = new();

    public event Action<Guid, string, string, IPAddress, int> PeerSeen; // id, name, group, addr, tcpPort
    public event Action<Guid, string, string> TextReceived;         // senderId, senderName, content
    public event Action<Guid, string, string, long> FileReceived;   // senderId, senderName, savedPath, size
    public event Action<string> Error;

    public NetworkService(string displayName) => DisplayName = displayName;

    public void Start()
    {
        // Prefer a fixed port so peers on other subnets can reach us by IP:port.
        // If it is taken (e.g. a second instance on this host), fall back to an OS
        // port — that instance is still found on the local subnet via broadcast.
        try { _tcp = new TcpListener(IPAddress.Any, RequestedPort); _tcp.Start(); }
        catch (SocketException) { _tcp = new TcpListener(IPAddress.Any, 0); _tcp.Start(); }
        TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.ExclusiveAddressUse = false;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort)); // shared port: multiple instances per host
        _udp.EnableBroadcast = true;
        _ = DiscoveryLoopAsync(_cts.Token);

        _broadcastTimer = new System.Threading.Timer(_ => Broadcast(), null, 0, 3000);
    }

    private void Broadcast()
    {
        try
        {
            string msg = $"{Protocol.DiscoveryMagic}|{Id}|{Uri.EscapeDataString(DisplayName)}|{TcpPort}|{Uri.EscapeDataString(Group)}";
            var data = Encoding.UTF8.GetBytes(msg);
            _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, Protocol.DiscoveryPort));
        }
        catch { /* transient network state; next tick retries */ }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(ct);
                var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
                if (parts.Length != 5 || parts[0] != Protocol.DiscoveryMagic) continue;
                if (!Guid.TryParse(parts[1], out var id) || id == Id) continue;   // ignore self
                string name = Uri.UnescapeDataString(parts[2]);
                if (!int.TryParse(parts[3], out int port)) continue;
                string group = Uri.UnescapeDataString(parts[4]);
                PeerSeen?.Invoke(id, name, group, r.RemoteEndPoint.Address, port);
            }
            catch (OperationCanceledException) { break; }
            catch { /* malformed datagram, keep listening */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                var type = (MessageType)Protocol.ReadInt32(ns);
                if (type == MessageType.Hello)
                {
                    var addr = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    ReadHello(ns, addr);     // learn who connected
                    WriteHello(ns);          // tell them who we are
                    return;
                }
                Guid.TryParse(Protocol.ReadStr(ns), out var senderId);
                string sender = Protocol.ReadStr(ns);
                if (type == MessageType.Text)
                {
                    string content = Protocol.ReadStr(ns);
                    TextReceived?.Invoke(senderId, sender, content);
                }
                else if (type == MessageType.File)
                {
                    string fileName = Path.GetFileName(Protocol.ReadStr(ns)); // strip any path -> no traversal
                    long len = Protocol.ReadInt64(ns);
                    string dir = string.IsNullOrWhiteSpace(DownloadDir)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SunChat_Received")
                        : DownloadDir;
                    Directory.CreateDirectory(dir);
                    string dest = UniquePath(Path.Combine(dir, fileName));
                    using (var fs = File.Create(dest))
                    {
                        byte[] buf = new byte[81920];
                        long rem = len;
                        while (rem > 0)
                        {
                            int n = ns.Read(buf, 0, (int)Math.Min(buf.Length, rem));
                            if (n <= 0) throw new EndOfStreamException("connection closed mid-file");
                            fs.Write(buf, 0, n);
                            rem -= n;
                        }
                    }
                    FileReceived?.Invoke(senderId, sender, dest, len);
                }
            }
        }
        catch (Exception ex) { Error?.Invoke($"수신 오류: {ex.Message}"); }
    }

    public async Task SendTextAsync(Peer p, string content)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(p.Address, p.TcpPort);
        using var ns = c.GetStream();
        Protocol.WriteInt32(ns, (int)MessageType.Text);
        Protocol.WriteStr(ns, Id.ToString());
        Protocol.WriteStr(ns, DisplayName);
        Protocol.WriteStr(ns, content);
        await ns.FlushAsync();
    }

    public async Task SendFileAsync(Peer p, string path, IProgress<long> progress)
    {
        var fi = new FileInfo(path);
        using var c = new TcpClient();
        await c.ConnectAsync(p.Address, p.TcpPort);
        using var ns = c.GetStream();
        Protocol.WriteInt32(ns, (int)MessageType.File);
        Protocol.WriteStr(ns, Id.ToString());
        Protocol.WriteStr(ns, DisplayName);
        Protocol.WriteStr(ns, fi.Name);
        Protocol.WriteInt64(ns, fi.Length);

        using var fs = File.OpenRead(path);
        byte[] buf = new byte[81920];
        long sent = 0;
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0)
        {
            await ns.WriteAsync(buf.AsMemory(0, n));
            sent += n;
            progress?.Report(sent);
        }
        await ns.FlushAsync();
    }

    // Is the peer's SunChat listener actually accepting connections right now?
    // A short TCP connect works across subnets, unlike broadcast.
    public static async Task<bool> IsAliveAsync(IPAddress addr, int port, int timeoutMs = 1000)
    {
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync(addr, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            return done == connect && c.Connected;
        }
        catch { return false; }
    }

    // Manually connect to a peer by IP/host:port (for peers that broadcast cannot
    // reach, e.g. a different subnet). Exchanges identity both ways.
    public async Task ConnectManualAsync(string host, int port)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(host, port);
        using var ns = c.GetStream();
        WriteHello(ns);                                   // introduce ourselves
        var addr = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
        if ((MessageType)Protocol.ReadInt32(ns) != MessageType.Hello)
            throw new InvalidDataException("예상치 못한 응답 (상대가 SunChat이 아님)");
        ReadHello(ns, addr);                              // learn who they are
    }

    private void WriteHello(Stream s)
    {
        Protocol.WriteInt32(s, (int)MessageType.Hello);
        Protocol.WriteStr(s, Id.ToString());
        Protocol.WriteStr(s, DisplayName);
        Protocol.WriteStr(s, Group);
        Protocol.WriteInt32(s, TcpPort);
    }

    private void ReadHello(Stream s, IPAddress addr)
    {
        string id = Protocol.ReadStr(s);
        string name = Protocol.ReadStr(s);
        string group = Protocol.ReadStr(s);
        int port = Protocol.ReadInt32(s);
        if (Guid.TryParse(id, out var g) && g != Id)
            PeerSeen?.Invoke(g, name, group, addr, port);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _broadcastTimer?.Dispose();
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Stop(); } catch { }
        _cts.Dispose();
    }
}
