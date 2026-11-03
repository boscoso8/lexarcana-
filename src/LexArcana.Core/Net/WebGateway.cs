using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace LexArcana.Net;

/// <summary>
/// La porta web del server: su <c>/ws</c> accetta i giocatori via WebSocket (browser, iPhone), sul resto
/// serve i file del client web da una cartella. Un piccolo server HTTP/1.1 fatto apposta: solo GET,
/// intestazioni al massimo di 8 KB, nessun percorso fuori dalla cartella.
/// </summary>
public sealed class WebGateway(IPAddress address, int port, Func<IConnection, CancellationToken, Task> acceptPlayer, string? webRoot) : IDisposable
{
    public const int MaxHeaderBytes = 8 * 1024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener = new(address, port);
    private readonly string? _root = webRoot is null ? null : Path.GetFullPath(webRoot);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task StartAsync(CancellationToken ct)
    {
        _listener.Start();
        return AcceptLoopAsync(ct);
    }

    public void Stop() => _listener.Stop();

    public void Dispose() => _listener.Dispose();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = HandleAsync(tcp, ct);
        }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        bool handedOver = false;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (request is null) return;
            var (method, path, headers) = request.Value;

            if (method != "GET") { await RespondAsync(stream, 405, "text/plain", "Solo GET."u8.ToArray(), ct); return; }

            if (path == "/ws")
            {
                if (!headers.TryGetValue("sec-websocket-key", out var key) ||
                    !headers.GetValueOrDefault("upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(stream, 400, "text/plain", "Serve una richiesta WebSocket."u8.ToArray(), ct);
                    return;
                }
                // SHA-1 qui non protegge nulla: è il calcolo fisso che RFC 6455 impone per accettare l'handshake.
#pragma warning disable CA5350
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
#pragma warning restore CA5350
                var reply = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct);
                var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
                handedOver = true;
                await acceptPlayer(new WebSocketConnection(socket, tcp.Client.RemoteEndPoint?.ToString() ?? "?"), ct);
                return;
            }

            await ServeFileAsync(stream, path, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException or WebSocketException)
        {
        }
        finally
        {
            if (!handedOver) tcp.Dispose();
        }
    }

    private async Task ServeFileAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        if (_root is null)
        {
            await RespondAsync(stream, 404, "text/plain; charset=utf-8", "Questo server non ha il client web: avvialo con --web <cartella>."u8.ToArray(), ct);
            return;
        }
        var relative = Uri.UnescapeDataString(path.Split('?', 2)[0]).TrimStart('/');
        if (relative.Length == 0 || relative.EndsWith('/')) relative += "index.html";
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        bool inside = full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!inside || relative.Split('/').Any(s => s.StartsWith('.')) || !File.Exists(full))
        {
            await RespondAsync(stream, 404, "text/plain; charset=utf-8", "Non trovato."u8.ToArray(), ct);
            return;
        }
        await RespondAsync(stream, 200, ContentType(full), await File.ReadAllBytesAsync(full, ct), ct);
    }

    public static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".ico" => "image/x-icon",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    };

    private static async Task RespondAsync(NetworkStream stream, int status, string type, byte[] body, CancellationToken ct)
    {
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    /// <summary>Legge la riga di richiesta e le intestazioni (nomi in minuscolo). Null se la richiesta è malformata o troppo lunga.</summary>
    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            // si legge solo fino alla fine delle intestazioni: per un GET non c'è altro, e un WebSocket comincia dopo la risposta 101
            var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return (first[0], first[1], headers);
        }
    }
}
