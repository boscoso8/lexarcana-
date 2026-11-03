using System.Net;
using System.Net.Sockets;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Ui;

namespace LexArcana.App;

/// <summary>Il terminale del Demiurgo: apre il server e conduce la partita.</summary>
internal static class DemiurgeConsole
{
    private const string Help = """
        Comandi del Demiurgo
          <testo>                            parla a tutti
          /narra <testo>                     narrazione, messa in evidenza per tutti
          /tira <dadi>                       tiro pubblico, es. /tira 2d10+4
          /segreto <dadi>                    tiro che vedi solo tu
          /sussurra <nome> <testo>           parla in privato a un giocatore
          /scheda <nome>                     mostra la scheda di un giocatore
          /imposta <nome> <campo> [valore]   modifica la scheda di un giocatore
          /chi                               chi è presente
          /espelli <nome> [motivo]           allontana un giocatore
          /fine                              chiudi la sessione
        Vedi anche tutti i sussurri e i tiri segreti dei giocatori.
        """;

    public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
    {
        var renderer = new MessageRenderer(p, Names.Demiurge);
        var address = options.Host is { } host ? IPAddress.Parse(host) : IPAddress.Any;
        var server = new GameServer(address, options.Port, m => Terminal.WriteLine(renderer.Render(m)), options.Password);
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile aprire la porta {options.Port}: {ex.Message}"));
            return 1;
        }

        await using (server)
        {
            Terminal.WriteLine($"{p.Gilt(p.Bold("LEX ARCANA"))}{p.Muted(" · Sessione aperta. Sei il Demiurgo.")}");
            Terminal.WriteLine(p.Muted($"In ascolto su {address}:{server.Port}" +
                (options.Password is null ? " · nessuna parola d'ordine" : " · con parola d'ordine")));
            Terminal.WriteLine(p.Muted("I giocatori si collegano con: ") +
                p.Ink($"lexarcana client --host <indirizzo> --porta {server.Port} --nome <nome>"));
            Terminal.WriteLine(p.Muted("Scrivi /aiuto per i comandi del Demiurgo."));

            using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
            await foreach (var line in Terminal.ReadLinesAsync(done.Token))
            {
                switch (line.Trim())
                {
                    case "/fine" or "/esci":
                        await done.CancelAsync();
                        break;
                    case "/aiuto" or "/?":
                        Terminal.WriteLine(p.Muted(Help));
                        break;
                    default:
                        if (await server.ExecuteAsync(line) is { } reply) Terminal.WriteLine(renderer.Render(reply));
                        break;
                }
            }

            // Se l'ingresso si chiude (es. server avviato in background) la sessione resta aperta fino a Ctrl+C.
            if (!done.IsCancellationRequested)
            {
                try { await Task.Delay(Timeout.Infinite, done.Token); }
                catch (OperationCanceledException) { }
            }
            Terminal.WriteLine(p.Muted("Chiusura della sessione…"));
        }
        return 0;
    }
}
