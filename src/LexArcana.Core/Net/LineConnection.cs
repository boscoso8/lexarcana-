using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>
/// Una connessione TCP che scambia righe di testo.
/// <list type="bullet">
/// <item>Le righe in arrivo oltre <see cref="MaxLineBytes"/> chiudono la connessione: nessuno può esaurire la memoria dell'altro capo.</item>
/// <item>Le righe in uscita passano da una coda con un solo scrittore. Se la coda si riempie perché l'altro capo
/// non legge, la connessione si chiude invece di rallentare tutti gli altri.</item>
/// </list>
/// </summary>
public sealed class LineConnection : IConnection
{
    public const int MaxLineBytes = 16 * 1024;
    public const int MaxQueuedLines = 512;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _writer;
    private readonly byte[] _buffer = new byte[4096];
    private int _start, _end;

    public LineConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _writer = WriteLoopAsync();
    }

    public string RemoteEndPoint => _tcp.Client.RemoteEndPoint?.ToString() ?? "?";

    /// <summary>Si attiva quando la connessione si chiude, da qualunque lato.</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Mette in coda una riga. Restituisce false se la connessione è chiusa o l'altro capo non legge più.</summary>
    public bool Post(string line)
    {
        if (_outbox.Writer.TryWrite(line)) return true;
        Close();
        return false;
    }

    /// <summary>Attende che le righe in coda siano state scritte (o che la connessione si chiuda).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        _outbox.Writer.TryComplete();
        try { await _writer.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    /// <summary>La prossima riga non vuota, o null quando l'altro capo chiude.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, linked.Token).ConfigureAwait(false);
                if (_end == 0)
                    return line.Length == 0 ? null : throw new ProtocolException("Connessione chiusa a metà messaggio.");
            }

            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int stop = newline < 0 ? _end : newline;
            line.Write(_buffer, _start, stop - _start);
            if (line.Length > MaxLineBytes) throw new ProtocolException("Messaggio troppo lungo.");
            _start = newline < 0 ? _end : newline + 1;

            if (newline >= 0)
            {
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                if (text.Length > 0) return text;
                line.SetLength(0);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
            {
                await _stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), _closed.Token).ConfigureAwait(false);
                if (_outbox.Reader.Count == 0) await _stream.FlushAsync(_closed.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            Close();
        }
    }

    public void Close()
    {
        _outbox.Writer.TryComplete();
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        _tcp.Close();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _writer.ConfigureAwait(false);
        _tcp.Dispose();
        _closed.Dispose();
    }
}
