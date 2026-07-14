// rgas-booth status endpoint — SPEC S-7. Diagnostics only, never an operator
// surface. Plain TCP with a hand-rolled HTTP response: HttpListener on
// 0.0.0.0 would need an HTTP.sys URL ACL (admin/netsh), which an appliance
// install shouldn't depend on.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RgasReceiver;

public sealed class StatusServer
{
    private readonly TcpListener _listener;
    private readonly Func<object> _snapshot;

    public StatusServer(int port, Func<object> snapshot)
    {
        _snapshot = snapshot;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "rgas-status" }.Start();
        Log.Info($"status endpoint on http://0.0.0.0:{port}/status (S-7)");
    }

    private void AcceptLoop()
    {
        while (true)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                using var stream = client.GetStream();
                // Drain the request line + headers (best effort; we answer everything).
                var buf = new byte[4096];
                try { stream.Read(buf, 0, buf.Length); } catch { }
                var json = JsonSerializer.Serialize(_snapshot());
                var body = Encoding.UTF8.GetBytes(json);
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\ncontent-type: application/json\r\n" +
                    $"content-length: {body.Length}\r\nconnection: close\r\n\r\n");
                stream.Write(head); stream.Write(body);
            }
            catch (Exception ex)
            {
                Log.Warn($"status request failed: {ex.Message}");
            }
        }
    }
}
