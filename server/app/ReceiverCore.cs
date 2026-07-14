// rgas-booth receiver core — SPEC S-2 (listen), S-3 (new connection replaces
// old), S-17 (connection history). Owns the listener, buffer, player, and
// status endpoint; the window (S-16) is just a viewer over this.
// Wire contract (client SPEC C-29): raw PCM s16le 48 kHz stereo on TCP :4953.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RgasReceiver;

public sealed record ConnEvent(DateTime At, string Kind, string Address);

public sealed class ReceiverCore : IDisposable
{
    private const int HistoryCap = 100;

    private readonly object _gate = new();
    private readonly List<ConnEvent> _history = new(); // S-17a, newest first
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private TcpClient? _current;
    private TcpListener? _listener;
    private long _totalConnections;
    private int _historyVersion;

    public Config Cfg { get; }
    public JitterBuffer Buffer { get; }
    public Player Player { get; private set; } = null!;
    public string SourceAddress { get; private set; } = "";
    public TimeSpan Uptime => _uptime.Elapsed;
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public int HistoryVersion => _historyVersion;

    public ReceiverCore(Config cfg)
    {
        Cfg = cfg;
        Buffer = new JitterBuffer(cfg.BufferMs);
    }

    public void Start()
    {
        Log.Info($"rgas-booth receiver starting: port {Cfg.ListenPort}, buffer {Cfg.BufferMs} ms");
        Player = new Player(Buffer, Cfg.OutputDeviceMatch);
        _ = new StatusServer(Cfg.StatusPort, StatusSnapshot);

        _listener = new TcpListener(IPAddress.Any, Cfg.ListenPort);
        _listener.Start();
        Log.Info($"listening for PCM on tcp://0.0.0.0:{Cfg.ListenPort} (S-2)");
        new Thread(AcceptLoop) { IsBackground = true, Name = "rgas-accept" }.Start();
    }

    public List<ConnEvent> History()
    {
        lock (_gate) return _history.ToList();
    }

    /// <summary>S-7 + S-17: one snapshot shape for the endpoint and the window.</summary>
    public object StatusSnapshot() => new
    {
        state = Buffer.State.ToString().ToLowerInvariant(),
        source = SourceAddress,
        source_connected = Buffer.SourceConnected,     // S-17c
        buffer_fill_ms = Buffer.FillMs,
        buffer_target_ms = Cfg.BufferMs,
        underruns = Buffer.Underruns,
        total_connections = TotalConnections,          // S-17a
        sound_bites = Buffer.SoundBites,               // S-17b
        output_device = Player.DeviceName,
        uptime_s = (long)Uptime.TotalSeconds,
    };

    private void AddEvent(string kind, string address)
    {
        lock (_gate)
        {
            _history.Insert(0, new ConnEvent(DateTime.Now, kind, address));
            if (_history.Count > HistoryCap) _history.RemoveAt(_history.Count - 1);
            _historyVersion++;
        }
    }

    private void AcceptLoop()
    {
        while (true) // latest connection wins (S-3)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch (Exception ex) { Log.Warn($"accept failed: {ex.Message}"); continue; }

            client.NoDelay = true;
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            lock (_gate)
            {
                if (_current is not null)
                {
                    Log.Info($"new source {remote} replaces {SourceAddress} (S-3)");
                    AddEvent("replaced", SourceAddress);
                    try { _current.Close(); } catch { }
                }
                _current = client;
                SourceAddress = remote;
            }
            Interlocked.Increment(ref _totalConnections);
            AddEvent("connected", remote);
            Log.Info($"source connected: {remote}");
            Buffer.SourceAttached();
            new Thread(() => ReadLoop(client, remote)) { IsBackground = true, Name = "rgas-read" }.Start();
        }
    }

    private void ReadLoop(TcpClient client, string remote)
    {
        var chunk = new byte[16384];
        try
        {
            var stream = client.GetStream();
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                Buffer.Write(chunk, 0, n);
        }
        catch { /* disconnects are normal life here */ }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, client))
                {
                    _current = null;
                    SourceAddress = "";
                    Buffer.SourceDetached();
                    AddEvent("disconnected", remote);
                    Log.Info($"source disconnected: {remote}");
                }
            }
            try { client.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
        Player?.Dispose();
    }
}
