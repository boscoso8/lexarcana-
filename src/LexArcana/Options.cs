using System.Globalization;
using System.Net;
using LexArcana.Campaigns;
using LexArcana.Game;
using LexArcana.Net;

namespace LexArcana.App;

internal enum Mode { Help, Version, Server, Client, Character, Campaign }

/// <summary>Le opzioni della riga di comando, per tutte le modalità.</summary>
internal sealed record Options
{
    public Mode Mode { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = GameServer.DefaultPort;
    public string? Name { get; init; }
    public string? Password { get; init; }
    public bool Color { get; init; } = true;
    public string? MapFile { get; init; }
    public string? CampaignFile { get; init; }
    public int Adventure { get; init; } = 1;
    /// <summary>client: il personaggio da portare; personaggio: dove salvarlo.</summary>
    public string? CharacterFile { get; init; }
    public bool RandomCharacter { get; init; }
    public int? Seed { get; init; }
    public int Adventures { get; init; } = 4;
    public string? Folder { get; init; }
    /// <summary>server: la porta web (WebSocket su /ws e, con WebRoot, il client web per browser e iPhone).</summary>
    public int? WebPort { get; init; }
    public string? WebRoot { get; init; }
    /// <summary>server: la parola d'ordine con cui si conduce da remoto, collegandosi con il nome Demiurgo.</summary>
    public string? DemiurgePassword { get; init; }

    public const string Usage = """
        lexarcana server [--porta 7777] [--indirizzo 0.0.0.0] [--parola-d-ordine <testo>]
                         [--campagna <campagna.json> [--avventura 1] | --mappa <file>]
                         [--porta-web 8080 [--web <cartella del client web>]] [--parola-demiurgo <testo>] [--senza-colori]
            Apre una sessione: tu sei il Demiurgo e conduci la partita da questo terminale.
            Con --campagna il gruppo parte dall'avventura scelta; /avventura <n> lo porta alla successiva.
            Con --porta-web i giocatori entrano anche dal browser, iPhone compreso: http://<indirizzo>:8080
            Con --parola-demiurgo si conduce anche da remoto: lexarcana client --nome Demiurgo --parola-d-ordine <quella>

        lexarcana client --nome <nome> | --personaggio <file.json>
                         [--host 127.0.0.1 | --host https://tavolo.esempio.it] [--porta 7777] [--parola-d-ordine <testo>] [--senza-colori]
            Ti collega alla sessione di un Demiurgo come giocatore (solo testo).
            Con --personaggio la tua scheda viene compilata da sola.

        lexarcana personaggio --nome <nome> [--file <nome>.json] [--casuale [--seme <numero>]]
            Crea un personaggio passo per passo (o a caso con --casuale) e lo salva in un file.

        lexarcana --versione
            Mostra la versione del gioco e del runtime .NET che contiene.

        lexarcana campagna [--seme <numero>] [--avventure 4] [--cartella campagne]
            Genera una campagna: il manuale del Demiurgo (campagna.md), il file per il server
            (campagna.json) e la mappa 3D di ogni avventura (avventura-N.txt).

        Il client 3D è un programma a parte: lexarcana-visione --personaggio <file.json> --host <indirizzo>
        """;

    public static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new() { Mode = Mode.Help };
        error = "";
        if (args is [] or ["-h" or "--help" or "aiuto", ..]) return true;
        if (args is ["--versione" or "versione" or "--version", ..])
        {
            options = new() { Mode = Mode.Version };
            return true;
        }

        Mode mode = args[0].ToLowerInvariant() switch
        {
            "server" => Mode.Server,
            "client" => Mode.Client,
            "personaggio" => Mode.Character,
            "campagna" => Mode.Campaign,
            _ => Mode.Help,
        };
        if (mode == Mode.Help) return Fail($"Modalità sconosciuta: {args[0]}. Usa server, client, personaggio o campagna.", out error);

        var o = new Options { Mode = mode };
        for (int i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            bool Int(out int value, int min, int max) => int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
            switch (flag)
            {
                case "--porta" or "-p" when mode is Mode.Server or Mode.Client:
                    if (!Int(out var port, 0, 65535)) return Fail("--porta vuole un numero tra 0 e 65535.", out error);
                    o = o with { Port = port };
                    break;
                case "--host" or "--indirizzo" when mode is Mode.Server or Mode.Client:
                    var host = Value();
                    if (string.IsNullOrWhiteSpace(host)) return Fail($"{flag} vuole un indirizzo.", out error);
                    if (mode == Mode.Server && !IPAddress.TryParse(host, out _)) return Fail("--indirizzo vuole un indirizzo IP, es. 0.0.0.0 o 127.0.0.1.", out error);
                    o = o with { Host = host };
                    break;
                case "--nome" or "-n" when mode is Mode.Client or Mode.Character:
                    o = o with { Name = Value() };
                    break;
                case "--parola-d-ordine" when mode is Mode.Server or Mode.Client:
                    var password = Value();
                    if (string.IsNullOrEmpty(password)) return Fail("--parola-d-ordine vuole un testo.", out error);
                    o = o with { Password = password };
                    break;
                case "--mappa" when mode == Mode.Server:
                    o = o with { MapFile = Value() };
                    if (string.IsNullOrWhiteSpace(o.MapFile)) return Fail("--mappa vuole il percorso di un file.", out error);
                    break;
                case "--campagna" when mode == Mode.Server:
                    o = o with { CampaignFile = Value() };
                    if (string.IsNullOrWhiteSpace(o.CampaignFile)) return Fail("--campagna vuole il percorso di campagna.json.", out error);
                    break;
                case "--avventura" when mode == Mode.Server:
                    if (!Int(out var adventure, 1, CampaignGenerator.MaxAdventures)) return Fail($"--avventura vuole un numero da 1 a {CampaignGenerator.MaxAdventures}.", out error);
                    o = o with { Adventure = adventure };
                    break;
                case "--personaggio" when mode == Mode.Client:
                case "--file" when mode == Mode.Character:
                    o = o with { CharacterFile = Value() };
                    if (string.IsNullOrWhiteSpace(o.CharacterFile)) return Fail($"{flag} vuole il percorso di un file.", out error);
                    break;
                case "--casuale" when mode == Mode.Character:
                    o = o with { RandomCharacter = true };
                    break;
                case "--seme" when mode is Mode.Character or Mode.Campaign:
                    if (!Int(out var seed, 0, int.MaxValue)) return Fail("--seme vuole un numero intero positivo.", out error);
                    o = o with { Seed = seed };
                    break;
                case "--avventure" when mode == Mode.Campaign:
                    if (!Int(out var count, CampaignGenerator.MinAdventures, CampaignGenerator.MaxAdventures))
                        return Fail($"--avventure vuole un numero da {CampaignGenerator.MinAdventures} a {CampaignGenerator.MaxAdventures}.", out error);
                    o = o with { Adventures = count };
                    break;
                case "--cartella" when mode == Mode.Campaign:
                    o = o with { Folder = Value() };
                    if (string.IsNullOrWhiteSpace(o.Folder)) return Fail("--cartella vuole un percorso.", out error);
                    break;
                case "--porta-web" when mode == Mode.Server:
                    if (!Int(out var webPort, 0, 65535)) return Fail("--porta-web vuole un numero tra 0 e 65535.", out error);
                    o = o with { WebPort = webPort };
                    break;
                case "--web" when mode == Mode.Server:
                    o = o with { WebRoot = Value() };
                    if (string.IsNullOrWhiteSpace(o.WebRoot) || !Directory.Exists(o.WebRoot)) return Fail("--web vuole la cartella del client web (quella con index.html).", out error);
                    break;
                case "--parola-demiurgo" when mode == Mode.Server:
                    var dp = Value();
                    if (string.IsNullOrEmpty(dp)) return Fail("--parola-demiurgo vuole un testo.", out error);
                    o = o with { DemiurgePassword = dp };
                    break;
                case "--senza-colori":
                    o = o with { Color = false };
                    break;
                default:
                    return Fail($"Opzione sconosciuta per «{args[0]}»: {flag}", out error);
            }
        }

        if (o.WebRoot is not null && o.WebPort is null) o = o with { WebPort = 8080 };
        // nei pacchetti il client web sta nella cartella "web" accanto all'eseguibile: lo si usa da solo
        if (o.WebPort is not null && o.WebRoot is null && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "web")))
            o = o with { WebRoot = Path.Combine(AppContext.BaseDirectory, "web") };
        if (o.MapFile is not null && o.CampaignFile is not null) return Fail("Usa --campagna oppure --mappa, non entrambe.", out error);
        if (mode == Mode.Client && o.CharacterFile is null && !Names.IsValid(o.Name) && !Names.IsDemiurge(o.Name))
            return Fail($"Il client vuole --nome (da 1 a {Names.MaxLength} lettere o cifre, senza spazi) oppure --personaggio <file>.", out error);
        if (mode == Mode.Character && !Names.IsValid(o.Name))
            return Fail($"Serve --nome: il nome con cui entri in sessione, da 1 a {Names.MaxLength} lettere o cifre.", out error);

        options = o;
        return true;

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
