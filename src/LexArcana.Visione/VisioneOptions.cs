using System.Globalization;
using LexArcana.Characters;
using LexArcana.Game;
using LexArcana.Net;

namespace LexArcana.Visione;

internal sealed record VisioneOptions(string Host, int Port, string Name, string? Password, string? CapturePath, float CaptureAfter, (float X, float Z, float Yaw, float Pitch)? Frame = null, Character? Character = null, bool ShowSheet = false, string? CharacterPath = null, bool AutoFight = false)
{
    /// <summary>Comandi touch al posto di mouse e tastiera (browser su telefono o tablet).</summary>
    public bool Touch { get; init; }
    /// <summary>Chiede una riga di testo all'utente fuori dalla finestra 3D (nel browser: la tastiera di iOS). Null sul desktop.</summary>
    public Func<string?>? AskText { get; init; }
    /// <summary>Dove salvare il personaggio quando guadagna esperienza, se non c'è un file (nel browser: il suo archivio locale).</summary>
    public Action<Character>? SaveCharacter { get; init; }
    /// <summary>Le dita sullo schermo, lette dalla pagina: id, x, y per ognuna. Null: si usa il touch di raylib.</summary>
    public Func<double[]>? TouchPoints { get; init; }

    public const string Usage = """
        lexarcana-visione --personaggio <file.json> | --nome <nome> [--host 127.0.0.1 | --host https://tavolo.esempio.it] [--porta 7777] [--parola-d-ordine <testo>]
            Entra nella sessione di un Demiurgo e guarda il mondo con gli occhi del tuo personaggio.
            Il personaggio si crea con: lexarcana personaggio --nome <nome>
        lexarcana-visione --versione
            Mostra la versione del gioco e del runtime .NET che contiene.

        Opzioni per provare il rendering:
            --cattura <file.png>   salva un'immagine della finestra ed esce
            --dopo <secondi>       quando scattare la cattura (predefinito 3)
            --inquadra x,z,yaw,pitch   con --cattura: guarda da questo punto (metri, radianti) senza muoversi in rete
            --mostra-scheda        tiene aperta la scheda del personaggio (come il tasto C)
            --combatti             prova automatica: il personaggio va incontro al nemico più vicino e lo attacca
        """;

    public static bool TryParse(string[] args, out VisioneOptions options, out string error)
    {
        options = null!;
        error = "";
        string host = "127.0.0.1";
        string? name = null, password = null, capture = null;
        Character? character = null;
        bool showSheet = false;
        string? characterPath = null;
        bool autoFight = false;
        int port = GameServer.DefaultPort;
        float after = 3f;
        (float, float, float, float)? frame = null;
        for (int i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (flag)
            {
                case "--host": host = Value() ?? ""; break;
                case "--porta" or "-p":
                    if (!int.TryParse(Value(), CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                        return Fail("--porta vuole un numero tra 1 e 65535.", out error);
                    break;
                case "--nome" or "-n": name = Value(); break;
                case "--personaggio":
                    characterPath = Value() ?? "";
                    if (!CharacterFile.TryLoad(characterPath, out var loaded, out var characterError)) return Fail(characterError, out error);
                    character = loaded;
                    break;
                case "--parola-d-ordine": password = Value(); break;
                case "--cattura": capture = Value(); break;
                case "--dopo":
                    if (!float.TryParse(Value(), CultureInfo.InvariantCulture, out after) || after is < 0 or > 600)
                        return Fail("--dopo vuole un numero di secondi tra 0 e 600.", out error);
                    break;
                case "--mostra-scheda": showSheet = true; break;
                case "--combatti": autoFight = true; break;
                case "--inquadra":
                    var parts = (Value() ?? "").Split(',');
                    if (parts.Length != 4 || !parts.All(p => float.TryParse(p, CultureInfo.InvariantCulture, out _)))
                        return Fail("--inquadra vuole quattro numeri: x,z,yaw,pitch", out error);
                    var v = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                    frame = (v[0], v[1], v[2], v[3]);
                    break;
                case "-h" or "--help" or "aiuto":
                    return Fail("", out error);
                default:
                    return Fail($"Opzione sconosciuta: {flag}", out error);
            }
        }
        if (string.IsNullOrWhiteSpace(host)) return Fail("--host vuole un indirizzo.", out error);
        name ??= character?.Player;
        if (!Names.IsValid(name)) return Fail($"Serve --personaggio <file> oppure --nome (da 1 a {Names.MaxLength} lettere o cifre, senza spazi).", out error);
        if (frame is not null && capture is null) return Fail("--inquadra si usa solo insieme a --cattura.", out error);
        if (autoFight && frame is not null) return Fail("--combatti e --inquadra non si usano insieme.", out error);
        options = new(host, port, name!, password, capture, after, frame, character, showSheet, characterPath, autoFight);
        return true;

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
