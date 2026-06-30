using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LANWebTerminalManager.Services;

public sealed class ReceiverServer : IDisposable
{
    private const int MaxRequestBytes = 128 * 1024 * 1024;

    private readonly Func<string, string, byte[], (int Status, byte[] Body)> _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ReceiverServer(Func<string, string, byte[], (int Status, byte[] Body)> handler)
    {
        _handler = handler;
    }

    public void Start(int port)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClient(client), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var request = ReadHttpRequest(stream);
                if (request is null)
                {
                    WriteResponse(stream, 400, Encoding.UTF8.GetBytes("{\"error\":\"invalid request\"}"));
                    return;
                }

                if (!TryParseRequest(request, out var method, out var path, out var body))
                {
                    WriteResponse(stream, 400, Encoding.UTF8.GetBytes("{\"error\":\"bad request\"}"));
                    return;
                }

                var (status, responseBody) = _handler(method, path, body);
                WriteResponse(stream, status, responseBody);
            }
            catch
            {
                // ignore client errors
            }
        }
    }

    private static byte[]? ReadHttpRequest(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var marker = Encoding.ASCII.GetBytes("\r\n\r\n");

        while (buffer.Length < MaxRequestBytes)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);

            var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            var headerEnd = IndexOf(data, marker);
            if (headerEnd < 0) continue;

            var headerText = Encoding.UTF8.GetString(data[..headerEnd]);
            var contentLength = ParseContentLength(headerText);
            if (contentLength < 0 || contentLength > MaxRequestBytes)
            {
                return null;
            }

            var totalNeeded = headerEnd + marker.Length + contentLength;
            if (buffer.Length >= totalNeeded)
            {
                return data[..totalNeeded].ToArray();
            }
        }

        return null;
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;
            if (!line[..sep].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line[(sep + 1)..].Trim(), out var length)) return -1;
            return length;
        }

        return 0;
    }

    private static bool TryParseRequest(ReadOnlySpan<byte> data, out string method, out string path, out byte[] body)
    {
        method = "";
        path = "";
        body = [];

        var marker = Encoding.ASCII.GetBytes("\r\n\r\n");
        var index = IndexOf(data, marker);
        if (index < 0) return false;

        var headerText = Encoding.UTF8.GetString(data[..index]);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        method = parts[0];
        path = parts[1];

        var contentLength = ParseContentLength(headerText);
        if (contentLength < 0) return false;

        var bodyStart = index + marker.Length;
        if (data.Length < bodyStart + contentLength) return false;
        body = data.Slice(bodyStart, contentLength).ToArray();
        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        }

        return -1;
    }

    private static void WriteResponse(NetworkStream stream, int status, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "Error"
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        stream.Write(header);
        if (body.Length > 0) stream.Write(body);
    }
}
