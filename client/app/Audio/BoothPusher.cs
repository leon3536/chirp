// rgas-source booth push — SPEC C-29 (direct TCP), C-30 (continuous stream),
// C-31 (reconnect within 1 s, forever), C-31a (stop retrying when explicitly
// kicked by the booth), C-34 (retargetable after a scan), C-35 (booth absence
// is a warning, never an error).
//
// The AudioEngine hands every rendered block (including pure silence) to
// Enqueue(). A dedicated thread owns the socket: connect -> drain queue ->
// send; any failure closes the socket and retries after 1 s. The queue is
// bounded and drops OLDEST on overflow, so after an outage we resume near-live
// instead of replaying a backlog (the booth's jitter buffer trims the rest).
using System.Net.Sockets;

namespace RgasSoundboard.Audio;

public sealed class BoothPusher : IDisposable
{
    private const int MaxQueuedBlocks = 50; // 50 × 10 ms = 500 ms cap

    /// <summary>S-3a wire contract: the booth sends this right before closing our
    /// socket because another client took over. Must match ReceiverCore.KickMarker
    /// on the booth side. The PCM wire never carries anything booth->client
    /// otherwise, so this is unambiguous.</summary>
    private static readonly byte[] KickMarker = "RGAS-KICKED\n"u8.ToArray();

    private readonly Queue<byte[]> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0);
    private volatile bool _disposed;
    private volatile bool _connected;
    private volatile bool _retarget;
    private volatile bool _kicked;
    private volatile string _host = "";
    private volatile int _port = 4953;
    private volatile string _status = "No booth configured";

    public bool IsConnected => _connected;
    public string Status => _status;
    public string Host => _host;
    public event Action<bool>? ConnectionChanged;
    /// <summary>C-31a: fired once when the booth explicitly replaces us with
    /// another client. The push loop stops retrying after this — the UI owns
    /// telling the operator and closing the app.</summary>
    public event Action? Kicked;

    public BoothPusher(string host, int port)
    {
        _host = host?.Trim() ?? "";
        _port = port;
        new Thread(Run) { IsBackground = true, Name = "rgas-push", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>C-34: point at a newly configured/discovered booth without a restart.</summary>
    public void SetTarget(string host, int port)
    {
        _host = host?.Trim() ?? "";
        _port = port;
        _retarget = true;
        _available.Release(); // wake the send loop so it reconnects promptly
    }

    public void Enqueue(byte[] block)
    {
        lock (_gate)
        {
            if (_queue.Count >= MaxQueuedBlocks) _queue.Dequeue(); // drop oldest
            else _available.Release();
            _queue.Enqueue(block);
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            var host = _host;
            var port = _port;
            _retarget = false;

            if (string.IsNullOrWhiteSpace(host))
            {
                // C-35: unconfigured is a calm warning state, not an error.
                _status = "No booth configured — local playback still works";
                SetConnected(false);
                Thread.Sleep(300);
                continue;
            }

            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true, SendTimeout = 2000 };
                // Bounded connect: a raw Connect() to a dead IP can hang for ~20 s,
                // which would break the 1 s self-heal promise (C-31).
                if (!client.ConnectAsync(host, port).Wait(1500))
                    throw new TimeoutException();
                var stream = client.GetStream();
                _status = $"Streaming to booth {host}:{port}";
                SetConnected(true);

                // C-31a: we never expect any bytes back on this socket, so a
                // background reader exists solely to catch the S-3a kick marker
                // and set the volatile _kicked flag. It's torn down by the
                // client.Close() in `finally` below (that unblocks its Read).
                new Thread(() => { if (WaitForKick(stream)) _kicked = true; })
                    { IsBackground = true, Name = "rgas-push-kick" }.Start();

                while (!_disposed && !_retarget && !_kicked)
                {
                    if (!_available.Wait(500)) continue; // idle wake to notice disposal/retarget/kick
                    byte[]? block;
                    lock (_gate) block = _queue.Count > 0 ? _queue.Dequeue() : null;
                    if (block is not null) stream.Write(block, 0, block.Length);
                }
            }
            catch
            {
                // WiFi blip, booth reboot, wrong IP — all handled the same way (C-31)
                _status = $"Booth {host}:{port} unreachable — retrying";
            }
            finally
            {
                SetConnected(false);
                try { client?.Close(); } catch { }
            }

            if (_kicked)
            {
                // C-31a: deliberately replaced by another client — stop the
                // ping-pong. The UI (subscribed to Kicked) tells the operator
                // and closes the app; nothing left to do here.
                _status = "Session ended — another client connected to the booth";
                Kicked?.Invoke();
                return;
            }

            if (!_disposed && !_retarget) Thread.Sleep(1000); // C-31: retry within 1 s, forever
        }
    }

    /// <summary>Blocks reading the booth's push socket for the S-3a kick marker.
    /// Returns true only on a confirmed kick; any ordinary close/error/disposal
    /// returns false so the normal C-31 retry path runs unchanged.</summary>
    private static bool WaitForKick(NetworkStream stream)
    {
        try
        {
            var buf = new byte[KickMarker.Length];
            int total = 0;
            while (total < buf.Length)
            {
                int n = stream.Read(buf, total, buf.Length - total);
                if (n <= 0) return false; // plain EOF, not a kick
                total += n;
            }
            return buf.AsSpan().SequenceEqual(KickMarker);
        }
        catch
        {
            return false; // socket closed/disposed out from under us
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }

    public void Dispose()
    {
        _disposed = true;
        _available.Release();
    }
}
