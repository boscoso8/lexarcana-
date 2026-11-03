using System.Net;
using System.Net.Sockets;
using LexArcana.Campaigns;
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
          /avventura [numero]                elenca le avventure, o porta il gruppo in quella scelta
          /mappa <file>                      porta il gruppo in un luogo da un file di mappa
          /nemici                            elenca i nemici del luogo
          /evoca <tipo> [livello] [capo]     fa comparire un nemico (tipi: cultista, ladro, lupo, lemure, scheletro, strige, custode)
          /pace                              fa sparire tutti i nemici
          /cura [nome]                       guarisce un giocatore, o tutto il gruppo
          /fine                              chiudi la sessione
        Vedi anche tutti i sussurri e i tiri segreti dei giocatori.
        """;

    public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
    {
        var renderer = new MessageRenderer(Names.Demiurge);
        var address = options.Host is { } host ? IPAddress.Parse(host) : IPAddress.Any;
        var map = GameMap.Default;
        if (options.MapFile is { } file)
        {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, stop); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile leggere la mappa {file}: {ex.Message}"));
                return 1;
            }
            if (!GameMap.TryParse(lines, out map, out var error))
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"Mappa {file} non valida: {error}"));
                return 1;
            }
        }
        Campaign? campaign = null;
        if (options.CampaignFile is { } campaignFile)
        {
            if (!CampaignFiles.TryLoad(campaignFile, out var loaded, out var campaignError))
            {
                await Console.Error.WriteLineAsync(p.Pompeian(campaignError));
                return 1;
            }
            if (options.Adventure > loaded.Adventures.Length)
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"La campagna ha solo {loaded.Adventures.Length} avventure."));
                return 1;
            }
            campaign = loaded;
        }
        var server = new GameServer(address, options.Port, m => Terminal.WriteLine(p, renderer.Render(m)), options.Password, map: map, campaign: campaign, demiurgePassword: options.DemiurgePassword);
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
            var where = campaign is null ? $"nel luogo «{server.Map.Name}»" : $"con la campagna «{campaign.Title}»";
            Terminal.WriteLine($"{p.Gilt(p.Bold("LEX ARCANA"))}{p.Muted($" · Sessione aperta {where}. Sei il Demiurgo.")}");
            Terminal.WriteLine(p.Muted($"In ascolto su {address}:{server.Port}" +
                (options.Password is null ? " · nessuna parola d'ordine" : " · con parola d'ordine")));
            Terminal.WriteLine(p.Muted("I giocatori si collegano con: ") +
                p.Ink($"lexarcana client --host <indirizzo> --porta {server.Port} --nome <nome>") +
                p.Muted(" (testo) oppure lexarcana-visione con gli stessi argomenti (3D)"));
            if (options.WebPort is { } webPort)
            {
                try
                {
                    int actual = server.StartWeb(address, webPort, options.WebRoot);
                    Terminal.WriteLine(p.Muted($"Porta web {actual}: ") + p.Ink($"http://<indirizzo>:{actual}") +
                        p.Muted(options.WebRoot is null ? " (solo WebSocket su /ws: avvia con --web <cartella> per servire il client)" : " · dal browser, anche da iPhone"));
                }
                catch (SocketException ex)
                {
                    await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile aprire la porta web {webPort}: {ex.Message}"));
                    return 1;
                }
            }
            Terminal.WriteLine(p.Muted("Scrivi /aiuto per i comandi del Demiurgo."));
            if (campaign is not null)
            {
                Terminal.WriteLine(p.Muted($"{campaign.Adventures.Length} avventure · /avventura per l'elenco · il manuale completo è campagna.md accanto al file della campagna"));
                Terminal.WriteLine(p, renderer.Render(server.StartAdventure(options.Adventure)));
            }

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
                        if (await server.ExecuteAsync(line) is { } reply) Terminal.WriteLine(p, renderer.Render(reply));
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
