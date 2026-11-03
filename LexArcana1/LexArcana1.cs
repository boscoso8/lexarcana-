// LexArcana1.cs — Lex Arcana: server, client da terminale, personaggi e campagne in un solo file.
// Generato da tools/GeneraRed3D.cs: non modificarlo a mano, modifica i sorgenti in src/ e rigeneralo.
// Contiene 39 file sorgente. Compila con LexArcana1.csproj (net10.0, C# 14, nessuna dipendenza esterna).
//
// Avvio: dotnet run --project LexArcana1 -- server --porta-web 8080   (oppure: client, personaggio, campagna, --versione)
// Il client 3D è in Red3D.cs.

using LexArcana.App;
using LexArcana.Campaigns;
using LexArcana.Characters;
using LexArcana.Combat;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Protocol;
using LexArcana.Ui;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Channels;

// ===== src/LexArcana/Program.cs =====
Console.OutputEncoding = Encoding.UTF8;

if (!Options.TryParse(args, out var options, out var error))
{
    await Console.Error.WriteLineAsync($"{error}\n\n{Options.Usage}");
    return 2;
}

var palette = new Palette(options.Color && Palette.ShouldUseColor());
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

return options.Mode switch
{
    Mode.Server => await DemiurgeConsole.RunAsync(options, palette, stop.Token),
    Mode.Client => await PlayerConsole.RunAsync(options, palette, stop.Token),
    Mode.Character => await CharacterConsole.RunAsync(options, palette, stop.Token),
    Mode.Campaign => CampaignConsole.Run(options, palette),
    Mode.Version => PrintVersion(),
    _ => ShowUsage(),
};

static int ShowUsage()
{
    Console.WriteLine(Options.Usage);
    return 0;
}

static int PrintVersion()
{
    Console.WriteLine(LexArcana.Versions.Describe("Lex Arcana"));
    return 0;
}

// ===== src/LexArcana.Core/Campaigns/Campaign.cs =====
namespace LexArcana.Campaigns
{
    /// <summary>Un personaggio non giocante: chi è, come si riconosce e che cosa nasconde.</summary>
    public sealed record Npc(string Name, string Role, string Trait, string Secret);

    /// <summary>Un'avventura della campagna, con la mappa del suo luogo.</summary>
    public sealed record Adventure(
        int Number,
        string Title,
        PlaceKind Kind,
        string Place,
        string Hook,
        string Objective,
        string[] Scenes,
        Npc[] Npcs,
        string Complication,
        string Climax,
        string Reward,
        string[] Map);

    /// <summary>Una campagna: una minaccia che cresce di avventura in avventura fino allo scontro finale.</summary>
    public sealed record Campaign(
        int Seed,
        string Title,
        string Province,
        string City,
        string Threat,
        string Villain,
        string Patron,
        string Premise,
        Adventure[] Adventures)
    {
        public string ToJson() => JsonSerializer.Serialize(this, CampaignJson.Readable.Campaign);

        public static Campaign FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, CampaignJson.Default.Campaign) ?? throw new FormatException("File vuoto.");
            }
            catch (JsonException ex)
            {
                throw new FormatException("Il file della campagna non è valido: " + ex.Message, ex);
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(Campaign))]
    internal sealed partial class CampaignJson : JsonSerializerContext
    {
        public static CampaignJson Readable => field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

// ===== src/LexArcana.Core/Campaigns/CampaignFiles.cs =====
namespace LexArcana.Campaigns
{
    public static class CampaignFiles
    {
        /// <summary>Scrive la campagna in una cartella: campagna.json, campagna.md e avventura-N.txt. Restituisce i file scritti.</summary>
        public static IReadOnlyList<string> Write(Campaign campaign, string folder)
        {
            Directory.CreateDirectory(folder);
            List<string> written = [];
            void Save(string name, string content)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, content);
                written.Add(path);
            }
            Save("campagna.json", campaign.ToJson());
            Save("campagna.md", CampaignWriter.ToMarkdown(campaign));
            foreach (var adventure in campaign.Adventures) Save($"avventura-{adventure.Number}.txt", CampaignWriter.ToMapFile(adventure));
            return written;
        }

        public static bool TryLoad(string path, out Campaign campaign, out string error)
        {
            campaign = null!;
            error = "";
            try
            {
                campaign = Campaign.FromJson(File.ReadAllText(path));
                if (campaign.Adventures.Length == 0) error = "La campagna non ha avventure.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                error = $"Impossibile leggere la campagna {path}: {ex.Message}";
            }
            return error.Length == 0;
        }
    }
}

// ===== src/LexArcana.Core/Campaigns/CampaignGenerator.cs =====
namespace LexArcana.Campaigns
{
    /// <summary>
    /// Genera una campagna completa da un seme: con lo stesso seme e lo stesso numero di avventure
    /// esce sempre la stessa campagna, così il Demiurgo può rigenerarla o condividerla.
    /// Tutti i contenuti sono originali.
    /// </summary>
    public static class CampaignGenerator
    {
        public const int MinAdventures = 3, MaxAdventures = 7;

        private sealed record Threat(string Name, string Villain, PlaceKind Lair, string Goal, string Deadline, string[] Signs, string Artifact);

        private static readonly (string Province, string City)[] Places =
        [
            ("Britannia", "Londinium"), ("Aegyptus", "Alexandria"), ("Gallia", "Lugdunum"), ("Hispania", "Tarraco"),
            ("Syria", "Antiochia"), ("Achaia", "Corinthus"), ("Germania", "Colonia Agrippina"), ("Italia", "Neapolis"),
        ];

        private static readonly Threat[] Threats =
        [
            new("il Culto della Luna Nera", "un sacerdote rinnegato|una sacerdotessa rinnegata", PlaceKind.Tempio,
                "risvegliare una dea senza nome", "la prossima eclissi",
                ["sacerdotesse scomparse dal tempio di Diana", "falci di luna incise sulle porte delle case", "cani che ululano tutti insieme a mezzanotte"],
                "uno specchio d'ossidiana"),
            new("la Maledizione dei Calpurni", "l'ultimo erede di una famiglia tradita|l'ultima erede di una famiglia tradita", PlaceKind.Necropoli,
                "far estinguere tre famiglie senatorie", "le idi del prossimo mese",
                ["un senatore trovato morto con la bocca piena di terra", "lapidi che compaiono con nomi di persone ancora vive", "un bambino che parla con la voce di un'antenata"],
                "una tavoletta di piombo con una maledizione"),
            new("la Moneta che Sussurra", "un appaltatore delle miniere imperiali|un'appaltatrice delle miniere imperiali", PlaceKind.Miniera,
                "comprare l'anima di un'intera città", "la consegna del tributo all'imperatore",
                ["monete d'oro che i mercanti si rifiutano di toccare", "minatori che tornano in superficie senza ombra", "un banchiere che non dorme da trenta giorni"],
                "un conio d'oro antichissimo"),
            new("la Legione Perduta", "un centurione morto da vent'anni|", PlaceKind.Accampamento,
                "riprendersi le terre dove la sua legione fu massacrata", "il ventesimo anniversario della sconfitta",
                ["impronte di caligae nel fango che non portano da nessuna parte", "un'aquila di legione ritrovata in un pozzo", "sentinelle che sentono una tromba nella nebbia"],
                "l'aquila d'argento della legione"),
            new("lo Specchio di Ecate", "|una matrona che non invecchia", PlaceKind.Villa,
                "rubare la giovinezza alle fanciulle della città", "la festa dei Lemuria",
                ["giovani schiave che invecchiano in una notte", "uno specchio coperto in ogni stanza di una villa", "invitati a un banchetto che non ricordano di esserci stati"],
                "uno specchio di bronzo velato"),
            new("la Congiura delle Maschere", "un falso oracolo al servizio di senatori ambiziosi|una falsa sibilla al servizio di senatori ambiziosi", PlaceKind.Foro,
                "spingere la città alla rivolta contro il governatore", "il giorno delle elezioni dei magistrati",
                ["profezie scritte di notte sui muri del foro", "un magistrato che cambia voto all'improvviso", "maschere funebri rubate dagli atri delle case nobili"],
                "una maschera di cera che parla"),
            new("le Acque Nere", "un ingegnere degli acquedotti impazzito|un'ingegnera degli acquedotti impazzita", PlaceKind.Catacombe,
                "avvelenare la fonte sacra della città", "il solstizio d'estate",
                ["una fontana che getta acqua scura", "pesci morti nelle terme", "febbri che fanno sognare a tutti lo stesso sogno"],
                "un sigillo di bronzo delle chiuse"),
        ];

        private static readonly string[] Patrons =
        [
            "un legato imperiale|", "|la vestale più anziana della città", "un prefetto dei vigili|", "un senatore anziano e malato|",
            "il governatore della provincia|", "un sacerdote di Giove Ottimo Massimo|", "|una ricca vedova di mercanti", "un magistrato della città|una sacerdotessa di Cerere",
        ];

        private static readonly Dictionary<PlaceKind, string[]> PlaceNames = new()
        {
            [PlaceKind.Tempio] = ["il tempio di Diana", "il tempio di Mercurio", "il santuario di Iside", "il tempio di Saturno"],
            [PlaceKind.Foro] = ["il foro vecchio", "il foro delle corporazioni", "la basilica del foro"],
            [PlaceKind.Villa] = ["la villa dei {gens}", "la villa sul lago", "la villa suburbana dei {gens}"],
            [PlaceKind.Catacombe] = ["le gallerie sotto l'acquedotto", "le catacombe di via Appia", "le cisterne dimenticate"],
            [PlaceKind.Necropoli] = ["la necropoli fuori porta", "i sepolcri dei {gens}", "la città dei morti sulla collina"],
            [PlaceKind.Miniera] = ["la miniera d'argento abbandonata", "le cave di marmo", "i pozzi delle miniere imperiali"],
            [PlaceKind.Accampamento] = ["il vecchio castrum sul fiume", "l'accampamento della terza coorte", "il forte di confine"],
        };

        private static readonly string[] Gentes = ["Valerii", "Cornelii", "Aemilii", "Claudii", "Licinii", "Sempronii", "Domitii"];

        private static readonly string[] TitleOpenings = ["L'ombra", "Il silenzio", "Il sigillo", "Le voci", "Il sangue", "La soglia", "Il patto", "La veglia", "Il sonno"];

        private static readonly Dictionary<PlaceKind, string[]> SceneIdeas = new()
        {
            [PlaceKind.Tempio] = ["un rito interrotto a metà, con l'incenso ancora acceso", "una statua del dio con gli occhi cavati", "un archivio di ex voto che nasconde una lettera", "le sacerdotesse che negano tutto all'unisono"],
            [PlaceKind.Foro] = ["un comizio che degenera in rissa", "un banco dei cambiavalute con un doppio fondo", "un processo pubblico in cui il testimone chiave sparisce", "una statua onoraria appena sfregiata"],
            [PlaceKind.Villa] = ["un banchetto in cui nessuno mangia", "un giardino con un labirinto di siepi", "una biblioteca chiusa a chiave da dentro", "schiavi che parlano solo a gesti"],
            [PlaceKind.Catacombe] = ["un passaggio allagato fino al petto", "nicchie vuote dove dovrebbero esserci i morti", "graffiti di un culto proibito", "un rumore d'acqua che arriva dalla direzione sbagliata"],
            [PlaceKind.Necropoli] = ["un funerale celebrato di notte", "un mausoleo aperto dall'interno", "un custode del cimitero ubriaco che ha visto troppo", "offerte fresche su una tomba di cento anni fa"],
            [PlaceKind.Miniera] = ["un crollo che isola il gruppo", "minatori che si rifiutano di scendere oltre il terzo pozzo", "una vena d'oro dove non dovrebbe esserci", "i resti di un accampamento di schiavi fuggiti"],
            [PlaceKind.Accampamento] = ["un'ispezione che rivela armi mancanti", "un disertore catturato che implora di essere giustiziato", "il sacello delle insegne sorvegliato da soldati che nessuno conosce", "una marcia notturna nella nebbia"],
        };

        private static readonly Dictionary<PlaceKind, string[]> Roles = new()
        {
            [PlaceKind.Tempio] = ["|sacerdotessa", "custode del tempio|custode del tempio", "aruspice|", "venditore di amuleti|venditrice di amuleti", "schiavo del tempio|schiava del tempio"],
            [PlaceKind.Foro] = ["magistrato|", "banchiere|banchiera", "oratore|", "capo di una corporazione|", "pescivendolo|pescivendola"],
            [PlaceKind.Villa] = ["padrone di casa|padrona di casa", "liberto amministratore|liberta amministratrice", "medico di famiglia|medica di famiglia", "ospite straniero|ospite straniera", "cuoco|cuoca"],
            [PlaceKind.Catacombe] = ["fossore|", "ingegnere degli acquedotti|", "mendicante cieco|mendicante cieca", "ladro di tombe|ladra di tombe"],
            [PlaceKind.Necropoli] = ["custode del cimitero|custode del cimitero", "|prefica", "erede di una famiglia antica|erede di una famiglia antica", "imbalsamatore egizio|imbalsamatrice egizia"],
            [PlaceKind.Miniera] = ["capo minatore|", "sorvegliante|sorvegliante", "schiavo fuggito|schiava fuggita", "procuratore delle miniere|"],
            [PlaceKind.Accampamento] = ["centurione|", "vessillifero|", "medico militare|", "|vivandiera", "esploratore|esploratrice"],
        };

        private static readonly string[] Traits =
        [
            "parla sempre sottovoce", "ha una cicatrice che nasconde con cura", "ride nei momenti sbagliati", "porta un anello troppo prezioso per il suo rango",
            "cita poeti greci a ogni occasione", "ha paura del buio", "non guarda mai negli occhi", "ha una gentilezza sospetta", "zoppica da una gamba",
        ];

        private static readonly string[] Secrets =
        [
            "ha un debito enorme con la persona sbagliata", "fa la spia per il governatore", "ha un figlio prigioniero",
            "ha già visto il simbolo della minaccia, nell'infanzia", "ruba dalla cassa di cui ha la responsabilità", "ama in segreto uno dei nemici", "non è chi dice di essere",
        ];

        private static readonly string[] Complications =
        [
            "le guardie cittadine sospettano proprio del gruppo", "un temporale violento chiude le strade", "un rivale offre di più per lo stesso lavoro",
            "il committente mente su un dettaglio importante", "un alleato viene rapito", "un'epidemia mette in quarantena il quartiere", "un presagio infausto spaventa i testimoni",
        ];

        private static readonly string[] Rewards =
        [
            "il favore di una famiglia potente", "una borsa di sesterzi e un lasciapassare imperiale", "una lettera che apre le porte di una biblioteca proibita",
            "un'arma antica e ben bilanciata", "il debito di un magistrato", "un amuleto che protegge davvero", "la cittadinanza per chi non l'ha ancora",
        ];

        public static Campaign Generate(int seed, int adventures = 4)
        {
            adventures = Math.Clamp(adventures, MinAdventures, MaxAdventures);
            var random = new Random(seed);
            var (province, city) = Pick(random, Places);
            var threat = Pick(random, Threats);
            string villain = Person(random, threat.Villain);
            string patron = Person(random, Pick(random, Patrons));

            // I luoghi: tutti diversi, l'ultimo è sempre il covo della minaccia.
            var kinds = Enum.GetValues<PlaceKind>().Where(k => k != threat.Lair).OrderBy(_ => random.Next()).ToList();
            while (kinds.Count < adventures - 1) kinds.AddRange(kinds);
            var route = kinds.Take(adventures - 1).Append(threat.Lair).ToArray();

            var openings = TitleOpenings.OrderBy(_ => random.Next()).ToArray();
            var list = new Adventure[adventures];
            for (int i = 0; i < adventures; i++)
                list[i] = MakeAdventure(random, i + 1, adventures, route[i], threat, villain, patron, city, openings[i % openings.Length]);

            string premise =
                $"A {city}, nella provincia di {province}, qualcosa non va: {threat.Signs[0]}. " +
                $"Dietro c'è {threat.Name}, guidato da {villain}, che vuole {threat.Goal} entro {threat.Deadline}. " +
                $"{patron}, raduna il gruppo perché fermi tutto senza che la città se ne accorga.";

            return new Campaign(seed, $"{Capitalize(threat.Name)} a {city}", province, city, threat.Name, villain, patron, premise, list);
        }

        private static Adventure MakeAdventure(Random random, int number, int total, PlaceKind kind, Threat threat, string villain, string patron, string city, string opening)
        {
            bool first = number == 1, last = number == total;
            string place = Capitalize(Pick(random, PlaceNames[kind]).Replace("{gens}", Pick(random, Gentes), StringComparison.Ordinal));
            string sign = threat.Signs[(number - 1) % threat.Signs.Length];
            string patronName = patron.Split(',')[0];
            string villainName = villain.Split(',')[0];

            int count = random.Next(2, 4);
            var roles = Roles[kind].OrderBy(_ => random.Next()).Take(count).ToArray();
            var traits = Traits.OrderBy(_ => random.Next()).Take(count).ToArray();
            var secrets = Secrets.OrderBy(_ => random.Next()).Take(count).ToArray();
            var npcs = Enumerable.Range(0, count).Select(i =>
            {
                var person = Person(random, roles[i]).Split(", ", 2);
                return new Npc(person[0], person[1], traits[i], secrets[i]);
            }).ToArray();

            string hook = first
                ? $"{patronName} convoca il gruppo di notte: {sign}. Le tracce portano {To(place)}."
                : last
                    ? $"Tutti gli indizi portano {To(place)}: è lì che {villainName} prepara il rito finale."
                    : $"Un messaggio senza firma parla di {sign}. Qualcuno {In(place)} sa di più.";

            string objective = first
                ? $"Scoprire chi c'è dietro: {sign}."
                : last
                    ? $"Fermare {villainName} e distruggere {threat.Artifact} prima {Of(threat.Deadline)}."
                    : Pick(random, new[]
                    {
                        $"Recuperare un frammento {Of(threat.Artifact)} prima che lo trovino i seguaci di {villainName}.",
                        $"Proteggere {npcs[0].Name}, {npcs[0].Role}, che ha visto troppo.",
                        $"Seguire {npcs[0].Name} fino al nascondiglio e scoprire chi incontra.",
                        $"Trovare le prove che legano {villainName} {To(threat.Name)}.",
                    });

            var scenes = SceneIdeas[kind].OrderBy(_ => random.Next()).Take(3).ToArray();

            string climax = last
                ? $"Nel cuore {Of(place)}, davanti all'altare, {villainName} compie il rito. Se il gruppo arriva tardi, riesce a {threat.Goal}."
                : $"All'altare {Of(place)} il gruppo trova un indizio che porta all'avventura successiva, e un seguace di {villainName} che non vuole farsi prendere vivo.";

            string title = last
                ? $"{Capitalize(threat.Name)}"
                : $"{opening} {Of(place)}";

            return new Adventure(number, title, kind, place, hook, objective, scenes, npcs,
                Pick(random, Complications), climax, Pick(random, Rewards), MapGenerator.Generate(kind, random));
        }

        // ---- Piccoli aiuti di lingua italiana

        private static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

        private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

        /// <summary>
        /// Un nome romano con il suo ruolo, "Nome, ruolo". Il ruolo si scrive "maschile|femminile";
        /// se un lato è vuoto quel ruolo ha un solo genere, e il nome si accorda.
        /// </summary>
        private static string Person(Random random, string role)
        {
            var forms = role.Split('|');
            string male = forms[0], female = forms.Length > 1 ? forms[1] : forms[0];
            bool isFemale = male.Length == 0 || (female.Length > 0 && random.Next(2) == 0);
            return $"{RomanNames.Generate(random, isFemale)}, {(isFemale ? female : male)}";
        }

        private static string Articulate(string s, string bare, string[] forms)
        {
            var l = Lower(s);
            string[] articles = ["il ", "lo ", "la ", "i ", "gli ", "le ", "l'"];
            for (int i = 0; i < articles.Length; i++)
                if (l.StartsWith(articles[i], StringComparison.Ordinal)) return forms[i] + l[articles[i].Length..];
            return bare + l;
        }

        /// <summary>"il tempio" → "al tempio", "la villa" → "alla villa".</summary>
        private static string To(string s) => Articulate(s, "a ", ["al ", "allo ", "alla ", "ai ", "agli ", "alle ", "all'"]);

        /// <summary>"il tempio" → "nel tempio", "le gallerie" → "nelle gallerie".</summary>
        private static string In(string s) => Articulate(s, "in ", ["nel ", "nello ", "nella ", "nei ", "negli ", "nelle ", "nell'"]);

        /// <summary>"il tempio" → "del tempio", "la villa" → "della villa", "l'aquila" → "dell'aquila", "uno specchio" → "di uno specchio".</summary>
        private static string Of(string s) => Articulate(s, "di ", ["del ", "dello ", "della ", "dei ", "degli ", "delle ", "dell'"]);
    }
}

// ===== src/LexArcana.Core/Campaigns/CampaignWriter.cs =====
namespace LexArcana.Campaigns
{
    /// <summary>Il manuale del Demiurgo: la campagna in Markdown, da leggere o stampare prima della sessione.</summary>
    public static class CampaignWriter
    {
        public static string ToMarkdown(Campaign c)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {c.Title}").AppendLine();
            sb.AppendLine($"*Campagna generata con il seme {c.Seed} · {c.Adventures.Length} avventure · {c.City}, {c.Province}*").AppendLine();
            sb.AppendLine("## Premessa").AppendLine().AppendLine(c.Premise).AppendLine();
            sb.AppendLine($"- **Minaccia:** {c.Threat}");
            sb.AppendLine($"- **Antagonista:** {c.Villain}");
            sb.AppendLine($"- **Committente:** {c.Patron}").AppendLine();

            foreach (var a in c.Adventures)
            {
                sb.AppendLine($"## {a.Number}. {a.Title}").AppendLine();
                sb.AppendLine($"**Luogo:** {a.Place} ({a.Kind.ToString().ToLowerInvariant()}) · mappa: `avventura-{a.Number}.txt`").AppendLine();
                sb.AppendLine($"**Gancio.** {a.Hook}").AppendLine();
                sb.AppendLine($"**Obiettivo.** {a.Objective}").AppendLine();
                sb.AppendLine("**Scene possibili**").AppendLine();
                foreach (var scene in a.Scenes) sb.AppendLine($"- {Capitalize(scene)}.");
                sb.AppendLine().AppendLine("**Personaggi non giocanti**").AppendLine();
                sb.AppendLine("| Nome | Ruolo | Come si riconosce | Segreto |").AppendLine("| --- | --- | --- | --- |");
                foreach (var n in a.Npcs) sb.AppendLine($"| {n.Name} | {n.Role} | {n.Trait} | {n.Secret} |");
                sb.AppendLine().AppendLine($"**Complicazione.** {Capitalize(a.Complication)}.").AppendLine();
                sb.AppendLine($"**Climax.** {a.Climax}").AppendLine();
                sb.AppendLine($"**Ricompensa.** {Capitalize(a.Reward)}.").AppendLine();
                sb.AppendLine("```text");
                foreach (var row in a.Map) sb.AppendLine(row);
                sb.AppendLine("```").AppendLine();
            }

            sb.AppendLine("---").AppendLine();
            sb.AppendLine("Legenda delle mappe: `#` muro, `.` pavimento, `C` colonna, `A` altare (il cuore del luogo), `S` partenza del gruppo. Ogni casella misura 2 metri.");
            return sb.ToString();
        }

        /// <summary>Il file di mappa di un'avventura, nel formato che il server carica con --mappa.</summary>
        public static string ToMapFile(Adventure a) => $"nome: {a.Place}\n" + string.Join('\n', a.Map) + "\n";

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}

// ===== src/LexArcana.Core/Campaigns/MapGenerator.cs =====
namespace LexArcana.Campaigns
{
    /// <summary>Il tipo di luogo di un'avventura: decide la forma della mappa.</summary>
    public enum PlaceKind { Tempio, Foro, Villa, Catacombe, Necropoli, Miniera, Accampamento }

    /// <summary>
    /// Genera la mappa di un luogo nel formato di <see cref="GameMap"/> (# muro, . pavimento, C colonna, A altare, S partenza).
    /// Ogni mappa ha tre punti di partenza e un altare: il cuore del luogo, dove di solito si chiude l'avventura.
    /// Ogni casella di pavimento è raggiungibile dalle partenze.
    /// </summary>
    public static class MapGenerator
    {
        public static string[] Generate(PlaceKind kind, Random random) => kind switch
        {
            PlaceKind.Tempio => Hall(random, forum: false),
            PlaceKind.Foro => Hall(random, forum: true),
            PlaceKind.Villa => Peristyle(random),
            PlaceKind.Accampamento => Camp(random),
            _ => Rooms(random, kind),
        };

        // ---- Sala con colonnato (tempio, foro)

        private static string[] Hall(Random random, bool forum)
        {
            int w = Odd(random, forum ? 21 : 15, forum ? 27 : 21), d = random.Next(forum ? 13 : 10, forum ? 17 : 14);
            var g = new Grid(w, d + 5);
            g.Fill(1, 1, w - 2, d - 2, '.');
            int mid = w / 2;
            for (int z = 2; z <= d - 3; z += 2)
            {
                g[2, z] = 'C';
                g[w - 3, z] = 'C';
                if (forum && z > 2 && z < d - 3) { g[6, z] = 'C'; g[w - 7, z] = 'C'; }
            }
            if (forum)
            {
                // un recinto sacro al centro, con l'ingresso verso sud
                int top = 3, bottom = Math.Min(d - 4, 8);
                g.Rect(mid - 3, top, mid + 3, bottom, '#');
                g.Fill(mid - 2, top + 1, mid + 2, bottom - 1, '.');
                g[mid, bottom] = '.';
                g[mid, top + 2] = 'A';
            }
            else
            {
                g[mid, 3] = 'A';
                for (int z = 5; z < d - 2; z += 3) { g[mid - 2, z] = 'C'; g[mid + 2, z] = 'C'; }
            }
            // corridoio d'ingresso a sud con le partenze
            g.Fill(mid - 1, d - 1, mid + 1, d + 3, '.');
            g[mid, d + 2] = 'S';
            g[mid - 1, d + 3] = 'S';
            g[mid + 1, d + 3] = 'S';
            return g.Finish();
        }

        // ---- Villa con peristilio

        private static string[] Peristyle(Random random)
        {
            int w = Odd(random, 23, 29), d = Odd(random, 19, 23);
            var g = new Grid(w, d + 3);
            g.Fill(1, 1, w - 2, d - 2, '.');

            // il cortile, circondato da un muro con quattro aperture e da un anello di colonne
            int inset = 5;
            int x0 = inset, z0 = inset, x1 = w - 1 - inset, z1 = d - 1 - inset;
            g.Rect(x0 - 1, z0 - 1, x1 + 1, z1 + 1, '#');
            g[(x0 + x1) / 2, z0 - 1] = '.';
            g[(x0 + x1) / 2, z1 + 1] = '.';
            g[x0 - 1, (z0 + z1) / 2] = '.';
            g[x1 + 1, (z0 + z1) / 2] = '.';
            for (int x = x0 + 1; x <= x1 - 1; x += 2) { g[x, z0 + 1] = 'C'; g[x, z1 - 1] = 'C'; }
            for (int z = z0 + 3; z <= z1 - 3; z += 2) { g[x0 + 1, z] = 'C'; g[x1 - 1, z] = 'C'; }
            g[(x0 + x1) / 2, (z0 + z1) / 2] = 'A';  // il larario al centro del cortile

            // stanze lungo i lati nord e sud, separate da muri con una porta
            foreach (var (top, bottom) in new[] { (1, z0 - 2), (z1 + 2, d - 2) })
                for (int x = 1 + random.Next(4, 7); x < w - 4; x += random.Next(4, 7))
                {
                    for (int z = top; z <= bottom; z++) g[x, z] = '#';
                    g[x, top == 1 ? bottom : top] = '.';
                }

            // l'ingresso al centro del lato sud
            int mid = w / 2;
            for (int z = z1 + 2; z <= d - 2; z++) g[mid, z] = g[mid, z] == '#' ? '.' : g[mid, z];
            g[mid, d - 1] = '.';
            g.Fill(mid - 1, d, mid + 1, d + 2, '.');
            g[mid, d + 1] = 'S';
            g[mid - 1, d + 2] = 'S';
            g[mid + 1, d + 2] = 'S';
            return g.Finish();
        }

        // ---- Accampamento: baracche in griglia dentro la cinta, il sacello al centro

        private static string[] Camp(Random random)
        {
            int blocksX = random.Next(3, 5), blocksZ = random.Next(3, 4);
            int bw = 4, bd = 3, road = 2;
            int w = 2 + blocksX * bw + (blocksX + 1) * road, d = 2 + blocksZ * bd + (blocksZ + 1) * road;
            if (blocksX % 2 == 0) w++; // una via centrale da nord a sud
            var g = new Grid(w, d + 3);
            g.Fill(1, 1, w - 2, d - 2, '.');
            int mid = w / 2;
            for (int bx = 0; bx < blocksX; bx++)
                for (int bz = 0; bz < blocksZ; bz++)
                {
                    int x = 1 + road + bx * (bw + road) + (blocksX % 2 == 0 && bx >= blocksX / 2 ? 1 : 0);
                    int z = 1 + road + bz * (bd + road);
                    if (x <= mid && x + bw - 1 >= mid) continue; // la via principale resta libera
                    if (random.Next(5) > 0) g.Fill(x, z, x + bw - 1, z + bd - 1, '#');
                }
            g[mid, d / 2] = 'A';
            g[mid - 1, d / 2 - 1] = 'C';
            g[mid + 1, d / 2 - 1] = 'C';
            // la porta sud
            g.Fill(mid - 1, d - 1, mid + 1, d + 1, '.');
            g[mid, d] = 'S';
            g[mid - 1, d + 1] = 'S';
            g[mid + 1, d + 1] = 'S';
            return g.Finish();
        }

        // ---- Stanze e cunicoli (catacombe, necropoli, miniera)

        private readonly record struct Room(int X, int Z, int W, int D)
        {
            public int CenterX => X + W / 2;
            public int CenterZ => Z + D / 2;
            public bool Overlaps(Room o, int margin) =>
                X - margin < o.X + o.W && o.X - margin < X + W && Z - margin < o.Z + o.D && o.Z - margin < Z + D;
        }

        private static string[] Rooms(Random random, PlaceKind kind)
        {
            int w = random.Next(34, 44), d = random.Next(26, 34);
            bool mine = kind == PlaceKind.Miniera;
            var g = new Grid(w, d);
            List<Room> rooms = [];
            int wanted = random.Next(6, 10);
            for (int attempt = 0; attempt < 400 && rooms.Count < wanted; attempt++)
            {
                int rw = random.Next(mine ? 3 : 4, mine ? 7 : 9), rd = random.Next(mine ? 3 : 4, mine ? 6 : 8);
                var room = new Room(random.Next(2, w - rw - 2), random.Next(2, d - rd - 2), rw, rd);
                if (rooms.All(r => !r.Overlaps(room, 2))) rooms.Add(room);
            }

            // si collegano in ordine da ovest a est, più qualche passaggio in più per non fare un corridoio unico
            rooms.Sort((a, b) => a.CenterX.CompareTo(b.CenterX));
            foreach (var r in rooms) g.Fill(r.X, r.Z, r.X + r.W - 1, r.Z + r.D - 1, '.');
            for (int i = 1; i < rooms.Count; i++) Corridor(g, random, rooms[i - 1], rooms[i]);
            for (int extra = random.Next(1, 3); extra > 0 && rooms.Count > 3; extra--)
                Corridor(g, random, rooms[random.Next(rooms.Count)], rooms[random.Next(rooms.Count)]);

            // pilastri o tombe nelle stanze grandi, mai dove passa un corridoio
            foreach (var r in rooms.Where(r => r.W >= 6 && r.D >= 6))
            {
                if (kind == PlaceKind.Necropoli)
                    for (int x = r.X + 1; x < r.X + r.W - 1; x += 2) { Pillar(g, x, r.Z + 1); Pillar(g, x, r.Z + r.D - 2); }
                else
                {
                    Pillar(g, r.X + 1, r.Z + 1); Pillar(g, r.X + r.W - 2, r.Z + 1);
                    Pillar(g, r.X + 1, r.Z + r.D - 2); Pillar(g, r.X + r.W - 2, r.Z + r.D - 2);
                }
            }

            var start = rooms[0];
            g[start.CenterX, start.CenterZ] = 'S';
            g[start.CenterX - 1, start.CenterZ] = 'S';
            g[start.CenterX + 1, start.CenterZ] = 'S';
            var heart = rooms.MaxBy(r => Math.Abs(r.CenterX - start.CenterX) + Math.Abs(r.CenterZ - start.CenterZ));
            g[heart.CenterX, heart.CenterZ] = 'A';
            return g.Finish();
        }

        private static void Corridor(Grid g, Random random, Room a, Room b)
        {
            int x = a.CenterX, z = a.CenterZ;
            bool horizontalFirst = random.Next(2) == 0;
            void Step(int tx, int tz)
            {
                while (x != tx) { x += Math.Sign(tx - x); if (g[x, z] != 'C') g[x, z] = '.'; }
                while (z != tz) { z += Math.Sign(tz - z); if (g[x, z] != 'C') g[x, z] = '.'; }
            }
            if (horizontalFirst) { Step(b.CenterX, z); Step(x, b.CenterZ); }
            else { Step(x, b.CenterZ); Step(b.CenterX, z); }
        }

        /// <summary>Mette una colonna solo dove non chiude un passaggio: tutte le caselle intorno devono essere pavimento.</summary>
        private static void Pillar(Grid g, int x, int z)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (g[x + dx, z + dz] != '.') return;
            g[x, z] = 'C';
        }

        private static int Odd(Random random, int min, int max) => random.Next(min, max + 1) | 1;

        /// <summary>Una griglia di caratteri; alla fine circonda il pavimento di muri e toglie i bordi vuoti.</summary>
        private sealed class Grid(int width, int depth)
        {
            private readonly char[,] _cells = Init(width, depth);

            private static char[,] Init(int w, int h)
            {
                var c = new char[w, h];
                for (int x = 0; x < w; x++) for (int z = 0; z < h; z++) c[x, z] = ' ';
                return c;
            }

            public char this[int x, int z]
            {
                get => x >= 0 && z >= 0 && x < width && z < depth ? _cells[x, z] : ' ';
                set { if (x >= 0 && z >= 0 && x < width && z < depth) _cells[x, z] = value; }
            }

            public void Fill(int x0, int z0, int x1, int z1, char c)
            {
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) this[x, z] = c;
            }

            public void Rect(int x0, int z0, int x1, int z1, char c)
            {
                for (int x = x0; x <= x1; x++) { this[x, z0] = c; this[x, z1] = c; }
                for (int z = z0; z <= z1; z++) { this[x0, z] = c; this[x1, z] = c; }
            }

            public string[] Finish()
            {
                // ogni casella vuota che tocca uno spazio aperto diventa muro
                var walls = new List<(int, int)>();
                for (int x = -1; x <= width; x++)
                    for (int z = -1; z <= depth; z++)
                        if (this[x, z] == ' ' && Neighbours(x, z).Any(n => n is '.' or 'S' or 'C' or 'A')) walls.Add((x, z));

                // si allarga di uno per far posto ai muri del bordo
                var rows = new List<string>();
                for (int z = -1; z <= depth; z++)
                {
                    var row = new char[width + 2];
                    for (int x = -1; x <= width; x++)
                        row[x + 1] = walls.Contains((x, z)) ? '#' : this[x, z];
                    rows.Add(new string(row).TrimEnd());
                }
                while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
                while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
                int left = rows.Where(r => r.Length > 0).Min(r => r.Length - r.TrimStart().Length);
                return [.. rows.Select(r => r.Length >= left ? r[left..] : r)];
            }

            private IEnumerable<char> Neighbours(int x, int z)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (dx != 0 || dz != 0) yield return this[x + dx, z + dz];
            }
        }
    }
}

// ===== src/LexArcana.Core/Characters/Character.cs =====
namespace LexArcana.Characters
{
    /// <summary>
    /// Un personaggio giocante. Si crea con <see cref="CharacterBuilder"/> (passo per passo) o con
    /// <see cref="Character.Random"/>, si salva come JSON e si porta in ogni sessione.
    /// </summary>
    public sealed record Character
    {
        /// <summary>Il nome con cui il giocatore entra in sessione (lettere e cifre, senza spazi).</summary>
        public required string Player { get; init; }
        /// <summary>Il nome romano del personaggio, es. "Aurelia Severa".</summary>
        public required string RomanName { get; init; }
        public required string Vocation { get; init; }
        public required string Origin { get; init; }
        /// <summary>I punti spesi su ogni attributo, prima dei bonus di vocazione e origine.</summary>
        public required Dictionary<Trait, int> BaseAttributes { get; init; }
        /// <summary>Le due abilità scelte oltre a quelle della vocazione.</summary>
        public required string[] ChosenSkills { get; init; }
        public string Concept { get; init; } = "";
        /// <summary>L'esperienza accumulata giocando; il livello si ricava da qui (vedi <see cref="Combat.Progression"/>).</summary>
        public int Experience { get; init; }

        [JsonIgnore]
        public int Level => Combat.Progression.LevelFor(Experience);

        [JsonIgnore]
        public Vocation VocationInfo => Rules.FindVocation(Vocation) ?? throw new InvalidOperationException($"Vocazione sconosciuta: {Vocation}");
        [JsonIgnore]
        public Origin OriginInfo => Rules.FindOrigin(Origin) ?? throw new InvalidOperationException($"Origine sconosciuta: {Origin}");

        /// <summary>Il valore finale di un attributo: punti + bonus della vocazione + bonus dell'origine.</summary>
        public int this[Trait attribute] =>
            BaseAttributes.GetValueOrDefault(attribute, Rules.AttributeMinimum)
            + (VocationInfo.Bonus == attribute ? 1 : 0)
            + (OriginInfo.Bonus == attribute ? 1 : 0);

        /// <summary>Il grado di un'abilità: 2 se viene dalla vocazione, 1 se scelta, 0 altrimenti (si sommano se coincidono).</summary>
        public int SkillRank(string skill) =>
            (VocationInfo.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase) ? Rules.VocationSkillRank : 0)
            + (ChosenSkills.Contains(skill, StringComparer.OrdinalIgnoreCase) ? Rules.ChosenSkillRank : 0);

        /// <summary>Il bonus totale per una prova: attributo dell'abilità + grado.</summary>
        public int CheckBonus(string skill) =>
            Rules.FindSkill(skill) is { } s ? this[s.Trait] + SkillRank(s.Name) : throw new ArgumentException($"Abilità sconosciuta: {skill}", nameof(skill));

        [JsonIgnore]
        public int Health => 8 + 2 * this[Trait.Robur];
        [JsonIgnore]
        public int Resolve => 4 + this[Trait.Pietas] + this[Trait.Ingenium];
        [JsonIgnore]
        public int Defense => 10 + this[Trait.Celeritas];

        /// <summary>Controlla che il personaggio rispetti le regole; restituisce gli errori, vuota se è valido.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = [];
            if (!Names.IsValid(Player)) errors.Add($"Il nome del giocatore deve avere 1–{Names.MaxLength} lettere o cifre, senza spazi.");
            if (Names.IsDemiurge(Player)) errors.Add($"«{Names.Demiurge}» è riservato a chi conduce la partita.");
            if (RomanName.Sanitized.Trim().Length is 0 or > 60) errors.Add("Il nome romano deve avere da 1 a 60 caratteri.");
            if (Rules.FindVocation(Vocation) is null) errors.Add($"Vocazione sconosciuta: {Vocation}.");
            if (Rules.FindOrigin(Origin) is null) errors.Add($"Origine sconosciuta: {Origin}.");
            if (Concept.Length > 160) errors.Add("Il concetto può avere al massimo 160 caratteri.");
            if (Experience is < 0 or > 1_000_000) errors.Add("L'esperienza deve stare tra 0 e 1.000.000.");

            foreach (var a in Rules.Traits)
            {
                int value = BaseAttributes.GetValueOrDefault(a, 0);
                if (value is < Rules.AttributeMinimum or > Rules.AttributeMaximumBeforeBonuses)
                    errors.Add($"{a} deve stare tra {Rules.AttributeMinimum} e {Rules.AttributeMaximumBeforeBonuses} prima dei bonus.");
            }
            int spent = Rules.Traits.Sum(a => BaseAttributes.GetValueOrDefault(a, 0) - Rules.AttributeMinimum);
            if (spent != Rules.AttributePoints) errors.Add($"Vanno spesi esattamente {Rules.AttributePoints} punti negli attributi (spesi: {spent}).");

            if (ChosenSkills.Length != Rules.ChosenSkills) errors.Add($"Scegli {Rules.ChosenSkills} abilità oltre a quelle della vocazione.");
            if (ChosenSkills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ChosenSkills.Length) errors.Add("Le abilità scelte devono essere diverse.");
            foreach (var skill in ChosenSkills.Where(s => Rules.FindSkill(s) is null)) errors.Add($"Abilità sconosciuta: {skill}.");
            return errors;
        }

        /// <summary>I campi della scheda condivisa in sessione, in ordine di lettura (ognuno sta nei limiti di <see cref="CharacterSheet"/>).</summary>
        public IEnumerable<(string Key, string Value)> ToSheet()
        {
            yield return ("Nome", RomanName);
            yield return ("Vocazione", Vocation);
            yield return ("Livello", Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ("Origine", $"{Origin} ({OriginInfo.Language})");
            foreach (var a in Rules.Traits) yield return (a.ToString(), this[a].ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ("Salute", $"{Health}/{Health}");
            yield return ("Volontà", $"{Resolve}/{Resolve}");
            yield return ("Difesa", Defense.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ("Abilità", string.Join(", ", Rules.Skills.Where(s => SkillRank(s.Name) > 0).Select(s => $"{s.Name} +{CheckBonus(s.Name)}")));
            yield return ("Equipaggiamento", string.Join(", ", VocationInfo.Equipment));
            if (Concept.Length > 0) yield return ("Concetto", Concept);
        }

        /// <summary>Un personaggio casuale ma valido. Con lo stesso seme esce sempre lo stesso.</summary>
        public static Character Random(string player, int seed) => Random(player, seed, null);

        /// <summary>Come <see cref="Random(string, int)"/>, ma con la vocazione scelta (se esiste).</summary>
        public static Character Random(string player, int seed, string? vocationName)
        {
            var random = new Random(seed);
            var vocation = Rules.Vocations[random.Next(Rules.Vocations.Count)];
            if (vocationName is not null && Rules.FindVocation(vocationName) is { } chosen) vocation = chosen;
            var origin = Rules.Origins[random.Next(Rules.Origins.Count)];

            // Più punti dove la vocazione ne ha bisogno, il resto a caso.
            var attributes = Rules.Traits.ToDictionary(a => a, _ => Rules.AttributeMinimum);
            var weights = Rules.Traits.ToDictionary(a => a, a =>
                1 + (a == vocation.Bonus ? 3 : 0) + vocation.Skills.Count(s => Rules.FindSkill(s)!.Trait == a));
            for (int spent = 0; spent < Rules.AttributePoints;)
            {
                int roll = random.Next(weights.Values.Sum());
                var pick = weights.First(w => (roll -= w.Value) < 0).Key;
                if (attributes[pick] >= Rules.AttributeMaximumBeforeBonuses) continue;
                attributes[pick]++;
                spent++;
            }

            var others = Rules.Skills.Where(s => !vocation.Skills.Contains(s.Name)).OrderBy(_ => random.Next()).Take(Rules.ChosenSkills);
            bool female = random.Next(2) == 0;
            return new Character
            {
                Player = player,
                RomanName = RomanNames.Generate(random, female),
                Vocation = vocation.Name,
                Origin = origin.Name,
                BaseAttributes = attributes,
                ChosenSkills = [.. others.Select(s => s.Name)],
                Concept = vocation.Description,
            };
        }

        // ---- File

        public string ToJson() => JsonSerializer.Serialize(this, CharacterJson.Readable.Character);

        public static Character FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, CharacterJson.Default.Character) ?? throw new FormatException("File vuoto.");
            }
            catch (JsonException ex)
            {
                throw new FormatException("Il file del personaggio non è valido: " + ex.Message, ex);
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(Character))]
    internal sealed partial class CharacterJson : JsonSerializerContext
    {
        /// <summary>Come Default, ma scrive le lettere accentate così come sono: i file restano leggibili e modificabili a mano.</summary>
        public static CharacterJson Readable => field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

// ===== src/LexArcana.Core/Characters/CharacterBuilder.cs =====
namespace LexArcana.Characters
{
    /// <summary>
    /// Crea un personaggio un passo alla volta, controllando le regole a ogni scelta.
    /// È la logica del creatore guidato; l'interfaccia (terminale o altro) chiede soltanto.
    /// </summary>
    public sealed class CharacterBuilder(string player)
    {
        private readonly Dictionary<Trait, int> _attributes = Rules.Traits.ToDictionary(a => a, _ => Rules.AttributeMinimum);
        private readonly List<string> _skills = [];

        public string Player { get; } = player;
        public string? RomanName { get; private set; }
        public Vocation? Vocation { get; private set; }
        public Origin? Origin { get; private set; }
        public string Concept { get; set; } = "";

        public int PointsLeft => Rules.AttributePoints - _attributes.Values.Sum(v => v - Rules.AttributeMinimum);
        public int Base(Trait a) => _attributes[a];
        public IReadOnlyList<string> ChosenSkills => _skills;

        public void SetRomanName(string name) => RomanName = name.Trim();
        public void Choose(Vocation vocation) => Vocation = vocation;
        public void Choose(Origin origin) => Origin = origin;

        /// <summary>Imposta un attributo (punti prima dei bonus). Fallisce se esce dai limiti o non bastano i punti.</summary>
        public bool TrySetAttribute(Trait attribute, int value, out string error)
        {
            error = "";
            if (value is < Rules.AttributeMinimum or > Rules.AttributeMaximumBeforeBonuses)
            {
                error = $"Un attributo va da {Rules.AttributeMinimum} a {Rules.AttributeMaximumBeforeBonuses} prima dei bonus.";
                return false;
            }
            int cost = value - _attributes[attribute];
            if (cost > PointsLeft)
            {
                error = $"Restano solo {PointsLeft} punti.";
                return false;
            }
            _attributes[attribute] = value;
            return true;
        }

        /// <summary>Aggiunge un'abilità a scelta. Non può essere già della vocazione, né ripetuta.</summary>
        public bool TryChooseSkill(string name, out string error)
        {
            error = "";
            if (Rules.FindSkill(name) is not { } skill) error = $"Abilità sconosciuta: {name}.";
            else if (_skills.Count >= Rules.ChosenSkills) error = $"Hai già scelto {Rules.ChosenSkills} abilità.";
            else if (_skills.Contains(skill.Name)) error = $"Hai già scelto {skill.Name}.";
            else if (Vocation?.Skills.Contains(skill.Name) == true) error = $"{skill.Name} viene già dalla tua vocazione: scegline un'altra.";
            else _skills.Add(skill.Name);
            return error.Length == 0;
        }

        public void ClearSkills() => _skills.Clear();

        /// <summary>Il personaggio finito, se tutte le scelte sono state fatte e rispettano le regole.</summary>
        public bool TryBuild(out Character character, out IReadOnlyList<string> errors)
        {
            character = null!;
            List<string> missing = [];
            if (Vocation is null) missing.Add("Scegli una vocazione.");
            if (Origin is null) missing.Add("Scegli un'origine.");
            if (string.IsNullOrWhiteSpace(RomanName)) missing.Add("Dai un nome romano al personaggio.");
            if (missing.Count > 0)
            {
                errors = missing;
                return false;
            }

            character = new Character
            {
                Player = Player,
                RomanName = RomanName!,
                Vocation = Vocation!.Name,
                Origin = Origin!.Name,
                BaseAttributes = new(_attributes),
                ChosenSkills = [.. _skills],
                Concept = Concept.Trim(),
            };
            errors = character.Validate();
            return errors.Count == 0;
        }
    }
}

// ===== src/LexArcana.Core/Characters/CharacterFile.cs =====
namespace LexArcana.Characters
{
    public static class CharacterFile
    {
        /// <summary>Legge e controlla un personaggio salvato; in caso di problemi spiega quale.</summary>
        public static bool TryLoad(string path, out Character character, out string error)
        {
            character = null!;
            try
            {
                character = Character.FromJson(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                error = $"Impossibile leggere il personaggio {path}: {ex.Message}";
                return false;
            }
            var problems = character.Validate();
            error = problems.Count == 0 ? "" : $"Il personaggio in {path} non rispetta le regole: {string.Join(" ", problems)}";
            return problems.Count == 0;
        }

        /// <summary>Il nome di file proposto per un personaggio: il nome del giocatore in minuscolo.</summary>
        public static string DefaultPath(Character character) => character.Player.ToLowerInvariant() + ".json";
    }
}

// ===== src/LexArcana.Core/Characters/RomanNames.cs =====
namespace LexArcana.Characters
{
    /// <summary>Nomi romani plausibili: praenomen, nomen e cognomen per gli uomini, nomen e cognomen al femminile per le donne.</summary>
    public static class RomanNames
    {
        private static readonly string[] Praenomina = ["Gaius", "Lucius", "Marcus", "Publius", "Quintus", "Titus", "Aulus", "Gnaeus", "Sextus", "Decimus", "Servius", "Spurius"];
        private static readonly string[] Nomina = ["Iulius", "Cornelius", "Valerius", "Aemilius", "Claudius", "Fabius", "Licinius", "Sempronius", "Antonius", "Flavius", "Aurelius", "Caecilius", "Pompeius", "Sulpicius", "Vipsanius", "Domitius", "Terentius", "Marius"];
        private static readonly string[] Cognomina = ["Rufus", "Niger", "Longus", "Severus", "Felix", "Crispus", "Paullus", "Varro", "Celer", "Priscus", "Maximus", "Lentulus", "Corvus", "Catulus", "Naso", "Saturninus", "Verus", "Gallus"];

        public static string Generate(Random random, bool female)
        {
            var nomen = Pick(random, Nomina);
            var cognomen = Pick(random, Cognomina);
            return female ? $"{Feminine(nomen)} {Feminine(cognomen)}" : $"{Pick(random, Praenomina)} {nomen} {cognomen}";
        }

        private static string Feminine(string name) =>
            name.EndsWith("us", StringComparison.Ordinal) ? name[..^2] + "a" :
            name.EndsWith("er", StringComparison.Ordinal) ? name[..^2] + "ra" :
            name.EndsWith('o') ? name + "nia" :
            name.EndsWith('x') ? name[..^1] + "cia" :
            name + "a";

        private static string Pick(Random random, string[] items) => items[random.Next(items.Length)];
    }
}

// ===== src/LexArcana.Core/Characters/Rules.cs =====
namespace LexArcana.Characters
{
    // Un sistema di regole originale e leggero.
    // Prova: 1d20 + attributo + abilità contro una difficoltà (10 facile, 15 normale, 20 difficile).

    /// <summary>I cinque attributi di un personaggio.</summary>
    public enum Trait
    {
        /// <summary>Forza, resistenza, combattimento corpo a corpo.</summary>
        Robur,
        /// <summary>Agilità, riflessi, mira.</summary>
        Celeritas,
        /// <summary>Intelligenza, memoria, ragionamento.</summary>
        Ingenium,
        /// <summary>Parola, presenza, capacità di convincere.</summary>
        Facundia,
        /// <summary>Devozione, forza d'animo, legame con il sacro.</summary>
        Pietas,
    }

    /// <summary>Un'abilità addestrabile, legata a un attributo.</summary>
    public sealed record Skill(string Name, Trait Trait, string Description);

    /// <summary>Una vocazione: il mestiere del personaggio prima che il mistero lo trovasse.</summary>
    public sealed record Vocation(string Name, Trait Bonus, string[] Skills, string[] Equipment, string Description);

    /// <summary>La provincia d'origine.</summary>
    public sealed record Origin(string Name, Trait Bonus, string Language, string Description);

    public static class Rules
    {
        public const int AttributeMinimum = 1;
        public const int AttributeMaximumBeforeBonuses = 5;
        public const int AttributeMaximum = 7;
        /// <summary>Punti da distribuire oltre al minimo di 1 in ogni attributo.</summary>
        public const int AttributePoints = 10;
        public const int VocationSkillRank = 2;
        public const int ChosenSkillRank = 1;
        public const int ChosenSkills = 2;

        public static IReadOnlyList<Trait> Traits { get; } = Enum.GetValues<Trait>();

        public static string Describe(Trait a) => a switch
        {
            Trait.Robur => "forza e resistenza",
            Trait.Celeritas => "agilità e mira",
            Trait.Ingenium => "intelletto e memoria",
            Trait.Facundia => "parola e presenza",
            Trait.Pietas => "devozione e forza d'animo",
            _ => "",
        };

        public static IReadOnlyList<Skill> Skills { get; } =
        [
            new("Armi", Trait.Robur, "combattere con gladio, lancia, scudo"),
            new("Atletica", Trait.Robur, "correre, arrampicarsi, nuotare, sollevare"),
            new("Furtività", Trait.Celeritas, "muoversi senza farsi notare, scassinare"),
            new("Tiro", Trait.Celeritas, "arco, fionda, giavellotto"),
            new("Cavalcare", Trait.Celeritas, "cavalli, carri, fughe"),
            new("Medicina", Trait.Ingenium, "ferite, veleni, malattie"),
            new("Erudizione", Trait.Ingenium, "storia, leggi, lingue antiche"),
            new("Indagine", Trait.Ingenium, "indizi, documenti, menzogne"),
            new("Persuasione", Trait.Facundia, "convincere, sedurre, intimidire"),
            new("Comando", Trait.Facundia, "guidare uomini, imporsi"),
            new("Commercio", Trait.Facundia, "trattare, valutare, corrompere"),
            new("Riti", Trait.Pietas, "preghiere, sacrifici, protezioni"),
            new("Presagi", Trait.Pietas, "leggere segni, sogni, voli di uccelli"),
            new("Sopravvivenza", Trait.Pietas, "orientarsi, cacciare, resistere alla fame"),
        ];

        public static IReadOnlyList<Vocation> Vocations { get; } =
        [
            new("Legionario", Trait.Robur, ["Armi", "Atletica", "Comando"],
                ["gladio", "scudo", "lorica", "pilum", "razioni per tre giorni"],
                "Ha marciato ai confini dell'impero e ha visto cose che i rapporti non raccontano."),
            new("Augure", Trait.Pietas, ["Presagi", "Riti", "Erudizione"],
                ["lituo", "tavolette cerate", "incenso", "amuleto di bronzo"],
                "Legge la volontà degli dei nel volo degli uccelli, e a volte gli dei rispondono."),
            new("Medico", Trait.Ingenium, ["Medicina", "Erudizione", "Persuasione"],
                ["borsa di strumenti", "erbe e unguenti", "rotolo di Ippocrate"],
                "Ha curato senatori e schiavi, e sa che i corpi raccontano segreti."),
            new("Esploratore", Trait.Celeritas, ["Furtività", "Tiro", "Sopravvivenza"],
                ["arco e frecce", "mantello scuro", "corda", "acciarino"],
                "Va avanti alle colonne, dove le mappe finiscono."),
            new("Mercante", Trait.Facundia, ["Commercio", "Persuasione", "Indagine"],
                ["borsa di denari", "sigillo di famiglia", "bilancia", "lettere di credito"],
                "Conosce i porti, i prezzi e chi si può comprare."),
            new("Gladiatore", Trait.Robur, ["Armi", "Atletica", "Persuasione"],
                ["tridente o spada corta", "rete", "cicatrici"],
                "Ha conquistato la libertà nell'arena; il pubblico ancora ne grida il nome."),
            new("Erudito", Trait.Ingenium, ["Erudizione", "Indagine", "Riti"],
                ["codici e papiri", "stilo", "lanterna", "lettera di presentazione"],
                "Ha studiato ad Alessandria e ha letto ciò che altri hanno bruciato."),
        ];

        public static IReadOnlyList<Origin> Origins { get; } =
        [
            new("Italia", Trait.Facundia, "latino", "Cresciuto all'ombra del Foro, tra clientele e favori."),
            new("Gallia", Trait.Robur, "gallico", "Discendente di guerrieri, romano da due generazioni."),
            new("Hispania", Trait.Celeritas, "iberico", "Viene dalle miniere e dalle coste dell'ovest."),
            new("Aegyptus", Trait.Ingenium, "greco ed egizio", "Ha visto i templi del Nilo e le loro biblioteche."),
            new("Achaia", Trait.Facundia, "greco", "Educato alla retorica e alla filosofia."),
            new("Britannia", Trait.Pietas, "britannico", "Viene da un'isola di nebbie e boschi sacri."),
            new("Syria", Trait.Ingenium, "aramaico e greco", "Conosce le carovane e i culti d'oriente."),
            new("Germania", Trait.Robur, "germanico", "Nato oltre il limes, ha scelto Roma."),
        ];

        public static Skill? FindSkill(string name) => Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public static Vocation? FindVocation(string name) => Vocations.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public static Origin? FindOrigin(string name) => Origins.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

// ===== src/LexArcana.Core/Combat/Bestiary.cs =====
namespace LexArcana.Combat
{
    /// <summary>
    /// Un tipo di nemico al livello 1. Le statistiche crescono con il livello (vedi <see cref="Enemy"/>).
    /// Speed in m/s; Reach, Aggro in metri.
    /// </summary>
    public sealed record EnemyKind(
        string Id, string Name, string EliteName,
        int BaseHealth, int HealthPerLevel, int BaseAttack, int BaseDefense, int DamageDie,
        float Speed, float Reach, float Aggro, int Experience, string Description);

    /// <summary>I nemici, presi dal folclore romano. Tutti originali nelle statistiche.</summary>
    public static class Bestiary
    {
        public static EnemyKind Cultist { get; } = new("cultista", "Cultista", "Gran sacerdote del culto", 8, 4, 2, 11, 6, 2.6f, 1.7f, 11f, 15, "Fanatici incappucciati, armati di falcetti rituali.");
        public static EnemyKind GraveRobber { get; } = new("ladro", "Ladro di tombe", "Capobanda", 7, 3, 3, 12, 6, 3.2f, 1.6f, 10f, 15, "Svelti e senza scrupoli, colpiscono e scappano.");
        public static EnemyKind BlackWolf { get; } = new("lupo", "Lupo nero", "Lupo capobranco", 9, 4, 3, 12, 6, 4.2f, 1.5f, 13f, 18, "Più grandi dei lupi veri, con occhi che riflettono il fuoco.");
        public static EnemyKind Lemur { get; } = new("lemure", "Lemure", "Lemure antico", 10, 4, 3, 13, 8, 2.2f, 1.8f, 12f, 20, "Uno spirito inquieto dei morti senza sepoltura.");
        public static EnemyKind Skeleton { get; } = new("scheletro", "Legionario scheletrico", "Centurione scheletrico", 12, 5, 3, 13, 8, 2.4f, 1.9f, 10f, 22, "Soldati caduti che marciano ancora, con lo scudo in mano.");
        public static EnemyKind Strix { get; } = new("strige", "Strige", "Strige regina", 6, 3, 4, 14, 4, 4.5f, 1.6f, 14f, 18, "Uccello notturno con artigli di bronzo e voce di donna.");
        public static EnemyKind Guardian { get; } = new("custode", "Custode di bronzo", "Colosso di bronzo", 20, 8, 4, 15, 10, 1.8f, 2.2f, 9f, 40, "Un automa costruito per proteggere il cuore del luogo.");

        public static IReadOnlyList<EnemyKind> All { get; } = [Cultist, GraveRobber, BlackWolf, Lemur, Skeleton, Strix, Guardian];

        public static EnemyKind? Find(string idOrName) =>
            All.FirstOrDefault(k => k.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || k.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Chi si incontra in un luogo. Il primo della lista fa da guardiano all'altare.</summary>
        public static IReadOnlyList<EnemyKind> ForPlace(PlaceKind? kind) => kind switch
        {
            PlaceKind.Tempio => [Cultist, Lemur, Cultist],
            PlaceKind.Foro => [Cultist, GraveRobber, GraveRobber],
            PlaceKind.Villa => [Lemur, Cultist, Strix],
            PlaceKind.Catacombe => [Skeleton, Lemur, GraveRobber],
            PlaceKind.Necropoli => [Skeleton, Lemur, Strix],
            PlaceKind.Miniera => [Guardian, BlackWolf, Skeleton],
            PlaceKind.Accampamento => [Skeleton, BlackWolf, Skeleton],
            _ => [Cultist, BlackWolf, Lemur],
        };
    }
}

// ===== src/LexArcana.Core/Combat/CombatEngine.cs =====
namespace LexArcana.Combat
{
    /// <summary>Ciò che è successo in un combattimento; il server lo traduce in messaggi.</summary>
    public abstract record CombatEvent;
    /// <summary>I nemici sono cambiati del tutto (nuovo luogo, evocazione, pace): va mandato l'elenco intero.</summary>
    public sealed record EnemiesChanged : CombatEvent;
    public sealed record EnemiesMoved(IReadOnlyList<Enemy> Enemies) : CombatEvent;
    /// <summary>Un colpo, andato a segno o no. Attaccante e bersaglio sono un giocatore (per nome) o un nemico (per id).</summary>
    public sealed record Blow(
        string Attacker, int? AttackerEnemyId, string Target, int? TargetEnemyId,
        int Roll, int Total, int Against, bool Hit, bool Critical, int Damage,
        int Health, int MaxHealth, bool Defeated, bool Ranged) : CombatEvent;
    public sealed record VitalsChanged(Fighter Fighter) : CombatEvent;
    public sealed record LevelUp(Fighter Fighter) : CombatEvent;
    public sealed record Respawned(Fighter Fighter, Pose Pose) : CombatEvent;

    /// <summary>
    /// Il combattimento, tutto lato server: dove sono i nemici, chi inseguono, chi colpisce chi.
    /// Non sa nulla di rete; non è sicuro tra thread, quindi il chiamante lo usa sotto un lock.
    /// Prova: 1d20 + bonus contro Difesa; 20 naturale è critico (danni doppi), 1 naturale manca sempre.
    /// </summary>
    public sealed class CombatEngine(Dice dice, GameMap map)
    {
        public const double RespawnDelay = 6;
        public const float SafeRadius = 10f;
        private const float EnemyRadius = 0.35f, Separation = 0.9f;

        private readonly Dictionary<int, Enemy> _enemies = [];
        private readonly Dictionary<string, Fighter> _fighters = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId;

        public GameMap Map { get; private set; } = map;
        public IReadOnlyCollection<Enemy> Enemies => _enemies.Values;
        public IReadOnlyCollection<Fighter> Fighters => _fighters.Values;
        public Fighter? FighterOf(string name) => _fighters.GetValueOrDefault(name);
        public Enemy? EnemyOf(int id) => _enemies.GetValueOrDefault(id);

        public Fighter Join(string name, Characters.Character? character = null)
        {
            var fighter = new Fighter(name, character);
            _fighters[name] = fighter;
            return fighter;
        }

        public void Leave(string name)
        {
            _fighters.Remove(name);
            foreach (var e in _enemies.Values) if (name.IsSameName(e.Target)) e.Target = null;
        }

        public double PartyLevel => _fighters.Count == 0 ? 1 : _fighters.Values.Average(f => f.Level);

        public void Clear() => _enemies.Clear();

        /// <summary>
        /// Popola un luogo: nemici sparsi lontano dalle partenze, un guardiano più forte accanto all'altare
        /// e, nell'avventura finale, il boss. Il livello segue <see cref="Progression.EncounterLevel"/>.
        /// </summary>
        public IReadOnlyList<Enemy> Populate(GameMap newMap, PlaceKind? kind, int adventure, bool finalAdventure, string? bossName, Random random)
        {
            Map = newMap;
            _enemies.Clear();
            int level = Progression.EncounterLevel(adventure, PartyLevel);
            var table = Bestiary.ForPlace(kind);

            var floors = new List<Pose>();
            (float X, float Z)? altar = null;
            for (int x = 0; x < newMap.Width; x++)
                for (int z = 0; z < newMap.Depth; z++)
                {
                    var (cx, cz) = GameMap.CellCenter(x, z);
                    if (newMap[x, z] == Tile.Altar) altar = (cx, cz);
                    if (newMap[x, z] == Tile.Floor && newMap.Spawns.All(s => Distance(s.X, s.Z, cx, cz) > SafeRadius))
                        floors.Add(new Pose(cx, cz, (float)(random.NextDouble() * MathF.Tau)));
                }

            if (altar is var (ax, az))
            {
                var near = floors.OrderBy(f => Distance(f.X, f.Z, ax, az)).ToList();
                if (near.Count > 0) Summon(table[0], level + 1, EnemyRank.Elite, near[0]);
                if (finalAdventure && near.Count > 1) Summon(table[0], level + 2, EnemyRank.Boss, near[1], bossName);
            }

            int wanted = Math.Clamp(floors.Count / 18, 3, 9);
            foreach (var spot in floors.OrderBy(_ => random.Next()))
            {
                if (_enemies.Count >= wanted + (altar is null ? 0 : finalAdventure ? 2 : 1)) break;
                if (_enemies.Values.Any(e => Distance(e.Pose.X, e.Pose.Z, spot.X, spot.Z) < 3f)) continue;
                Summon(table[random.Next(table.Count)], Math.Max(1, level - random.Next(2)), EnemyRank.Normal, spot);
            }
            return [.. _enemies.Values];
        }

        public Enemy Summon(EnemyKind kind, int level, EnemyRank rank, Pose pose, string? name = null)
        {
            var enemy = new Enemy(++_nextId, kind, Math.Clamp(level, 1, Progression.MaxLevel), rank, pose, name);
            enemy.LastSent = pose;
            _enemies[enemy.Id] = enemy;
            return enemy;
        }

        /// <summary>Un giocatore colpisce un nemico. Se il colpo non si può tentare, <paramref name="refusal"/> dice perché.</summary>
        public List<CombatEvent> Attack(string player, Pose from, int enemyId, bool ranged, double now, out string? refusal)
        {
            List<CombatEvent> events = [];
            refusal = null;
            if (FighterOf(player) is not { } fighter) { refusal = "Non sei in combattimento."; return events; }
            if (fighter.IsDown) { refusal = "Sei a terra: aspetta di rialzarti."; return events; }
            if (now < fighter.NextAttackAt) return events; // troppo presto: il colpo si perde senza messaggi
            if (EnemyOf(enemyId) is not { IsDead: false } enemy) { refusal = "Quel nemico non c'è più."; return events; }

            float distance = Distance(from.X, from.Z, enemy.Pose.X, enemy.Pose.Z);
            float reach = ranged ? Fighter.RangedReach : Fighter.MeleeReach + EnemyRadius;
            if (distance > reach) { refusal = ranged ? "Troppo lontano anche per un tiro." : "Troppo lontano: avvicinati o tira (clic destro)."; return events; }
            if (!Map.HasLineOfSight(from.X, from.Z, enemy.Pose.X, enemy.Pose.Z)) { refusal = "Non hai un tiro libero."; return events; }

            fighter.NextAttackAt = now + Fighter.AttackCooldown;
            int bonus = ranged ? fighter.RangedBonus : fighter.MeleeBonus;
            var (roll, total, hit, critical) = Resolve(bonus, enemy.Defense);
            int damage = 0;
            if (hit)
            {
                int die = ranged ? Fighter.RangedDie : Fighter.MeleeDie;
                damage = Math.Max(1, dice.D(die) + (critical ? dice.D(die) : 0) + (ranged ? fighter.RangedDamageBonus : fighter.MeleeDamageBonus));
                enemy.Health = Math.Max(0, enemy.Health - damage);
            }
            enemy.Target ??= fighter.Name; // chi viene attaccato si gira verso chi lo attacca
            events.Add(new Blow(fighter.Name, null, enemy.Label, enemy.Id, roll, total, enemy.Defense, hit, critical, damage,
                enemy.Health, enemy.MaxHealth, enemy.IsDead, ranged));

            if (enemy.IsDead)
            {
                _enemies.Remove(enemy.Id);
                // l'esperienza va a tutto il gruppo: si combatte insieme
                foreach (var f in _fighters.Values)
                {
                    bool levelled = f.Gain(enemy.Experience);
                    events.Add(new VitalsChanged(f));
                    if (levelled) events.Add(new LevelUp(f));
                }
            }
            return events;
        }

        /// <summary>
        /// Un passo della simulazione. <paramref name="players"/> sono i giocatori presenti nella scena 3D
        /// (chi gioca solo da terminale non si muove e i nemici non lo vedono).
        /// </summary>
        public List<CombatEvent> Tick(double now, float dt, IReadOnlyDictionary<string, Pose> players)
        {
            List<CombatEvent> events = [];

            foreach (var f in _fighters.Values.Where(f => f.IsDown && f.RespawnAt > 0 && now >= f.RespawnAt))
            {
                f.Health = f.MaxHealth;
                f.RespawnAt = 0;
                events.Add(new Respawned(f, Map.Spawns[Math.Abs(f.Name.GetHashCode(StringComparison.OrdinalIgnoreCase)) % Map.Spawns.Length]));
                events.Add(new VitalsChanged(f));
            }

            List<Enemy> moved = [];
            foreach (var enemy in _enemies.Values)
            {
                var target = ChooseTarget(enemy, players);
                enemy.Target = target?.Name;
                var pose = enemy.Pose;

                if (target is not null)
                {
                    var tp = players[target.Name];
                    float dx = tp.X - pose.X, dz = tp.Z - pose.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                    float yaw = MathF.Atan2(dx, dz);
                    if (distance > enemy.Reach * 0.8f)
                    {
                        var (sx, sz) = Separate(enemy);
                        float step = enemy.Kind.Speed * dt / MathF.Max(distance, 0.001f);
                        pose = Map.Slide(pose, dx * step + sx * dt, dz * step + sz * dt, yaw);
                    }
                    else pose = pose with { Yaw = yaw };

                    if (distance <= enemy.Reach && now >= enemy.NextAttackAt)
                    {
                        enemy.NextAttackAt = now + (enemy.Rank == EnemyRank.Boss ? 1.3 : 1.6);
                        events.AddRange(Strike(enemy, target, now));
                    }
                }
                else if (Distance(pose.X, pose.Z, enemy.Home.X, enemy.Home.Z) > 1f)
                {
                    float dx = enemy.Home.X - pose.X, dz = enemy.Home.Z - pose.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                    float step = enemy.Kind.Speed * 0.5f * dt / distance;
                    pose = Map.Slide(pose, dx * step, dz * step, MathF.Atan2(dx, dz));
                }

                enemy.Pose = pose;
                if (Distance(pose.X, pose.Z, enemy.LastSent.X, enemy.LastSent.Z) > 0.03f || MathF.Abs(MathF.IEEERemainder(pose.Yaw - enemy.LastSent.Yaw, MathF.Tau)) > 0.05f)
                {
                    enemy.LastSent = pose;
                    moved.Add(enemy);
                }
            }
            if (moved.Count > 0) events.Insert(0, new EnemiesMoved(moved));
            return events;
        }

        private Fighter? ChooseTarget(Enemy enemy, IReadOnlyDictionary<string, Pose> players)
        {
            Fighter? best = null;
            float bestDistance = float.MaxValue;
            foreach (var (name, pose) in players)
            {
                if (FighterOf(name) is not { IsDown: false } fighter) continue;
                float d = Distance(enemy.Pose.X, enemy.Pose.Z, pose.X, pose.Z);
                bool current = name.IsSameName(enemy.Target);
                // un bersaglio nuovo va visto ed è entro la portata dello sguardo; quello già inseguito si lascia solo se scappa lontano
                if (d > enemy.Kind.Aggro * (current ? 1.6f : 1f)) continue;
                if (!current && !Map.HasLineOfSight(enemy.Pose.X, enemy.Pose.Z, pose.X, pose.Z)) continue;
                if (current) d -= 2f; // preferisce chi sta già inseguendo
                if (d < bestDistance) { best = fighter; bestDistance = d; }
            }
            return best;
        }

        private IEnumerable<CombatEvent> Strike(Enemy enemy, Fighter fighter, double now)
        {
            var (roll, total, hit, critical) = Resolve(enemy.Attack, fighter.Defense);
            int damage = 0;
            if (hit)
            {
                damage = Math.Max(1, dice.D(enemy.Kind.DamageDie) + (critical ? dice.D(enemy.Kind.DamageDie) : 0) + enemy.DamageBonus);
                fighter.Health = Math.Max(0, fighter.Health - damage);
            }
            if (fighter.IsDown)
            {
                fighter.RespawnAt = now + RespawnDelay;
                foreach (var e in _enemies.Values) if (fighter.Name.IsSameName(e.Target)) e.Target = null;
            }
            yield return new Blow(enemy.Label, enemy.Id, fighter.Name, null, roll, total, fighter.Defense, hit, critical, damage,
                fighter.Health, fighter.MaxHealth, fighter.IsDown, false);
            yield return new VitalsChanged(fighter);
        }

        private (int Roll, int Total, bool Hit, bool Critical) Resolve(int bonus, int defense)
        {
            int roll = dice.D(20);
            int total = roll + bonus;
            bool critical = roll == 20;
            return (roll, total, critical || (roll != 1 && total >= defense), critical);
        }

        /// <summary>Una piccola spinta per non ammucchiare i nemici uno sull'altro.</summary>
        private (float X, float Z) Separate(Enemy enemy)
        {
            float x = 0, z = 0;
            foreach (var other in _enemies.Values)
            {
                if (other == enemy) continue;
                float dx = enemy.Pose.X - other.Pose.X, dz = enemy.Pose.Z - other.Pose.Z, d = MathF.Sqrt(dx * dx + dz * dz);
                if (d is > 0.001f and < Separation) { x += dx / d * (Separation - d) * 3; z += dz / d * (Separation - d) * 3; }
            }
            return (x, z);
        }

        private static float Distance(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
    }
}

// ===== src/LexArcana.Core/Combat/Fighters.cs =====
namespace LexArcana.Combat
{
    public enum EnemyRank { Normal, Elite, Boss }

    /// <summary>Un nemico nel mondo. Lo crea e lo muove <see cref="CombatEngine"/>.</summary>
    public sealed class Enemy
    {
        internal Enemy(int id, EnemyKind kind, int level, EnemyRank rank, Pose pose, string? name)
        {
            Id = id;
            Kind = kind;
            Level = level;
            Rank = rank;
            Pose = pose;
            Home = pose;
            float multiplier = rank switch { EnemyRank.Elite => 1.6f, EnemyRank.Boss => 3f, _ => 1f };
            MaxHealth = (int)MathF.Round((kind.BaseHealth + kind.HealthPerLevel * (level - 1)) * multiplier);
            Health = MaxHealth;
            Attack = kind.BaseAttack + level / 2 + (int)rank;
            Defense = kind.BaseDefense + level / 2 + (int)rank;
            DamageBonus = level / 2 + (int)rank;
            Name = name ?? (rank == EnemyRank.Normal ? kind.Name : kind.EliteName);
        }

        public int Id { get; }
        public EnemyKind Kind { get; }
        public string Name { get; }
        public int Level { get; }
        public EnemyRank Rank { get; }
        public int MaxHealth { get; }
        public int Health { get; internal set; }
        public int Attack { get; }
        public int Defense { get; }
        public int DamageBonus { get; }
        public Pose Pose { get; internal set; }
        public Pose Home { get; }
        public bool IsDead => Health <= 0;
        public int Experience => Kind.Experience * Level * Rank switch { EnemyRank.Elite => 2, EnemyRank.Boss => 5, _ => 1 };
        public float Reach => Kind.Reach * (Rank == EnemyRank.Boss ? 1.25f : 1f);

        internal string? Target { get; set; }
        internal double NextAttackAt { get; set; }
        internal Pose LastSent { get; set; }

        public string Label => $"{Name} (liv. {Level})";
    }

    /// <summary>Un giocatore in combattimento: statistiche ricavate dal personaggio (o di base) e dal livello.</summary>
    public sealed class Fighter
    {
        public const int DefaultMelee = 3, DefaultRanged = 2, DefaultHealth = 14, DefaultDefense = 11;
        public const float MeleeReach = 2.4f, RangedReach = 16f;
        public const double AttackCooldown = 0.9;

        private readonly Character? _character;

        public Fighter(string name, Character? character)
        {
            Name = name;
            _character = character;
            Experience = character?.Experience ?? 0;
            Level = Progression.LevelFor(Experience);
            Health = MaxHealth;
        }

        public string Name { get; }
        public int Level { get; private set; }
        public int Experience { get; private set; }
        public int Health { get; internal set; }
        public bool IsDown => Health <= 0;
        internal double RespawnAt { get; set; }
        internal double NextAttackAt { get; set; }

        private int LevelBonus => (Level - 1) / 2;

        public int MaxHealth => (_character?.Health ?? DefaultHealth) + 3 * (Level - 1);
        public int Defense => (_character?.Defense ?? DefaultDefense) + LevelBonus;
        public int MeleeBonus => (_character?.CheckBonus("Armi") ?? DefaultMelee) + LevelBonus;
        public int RangedBonus => (_character?.CheckBonus("Tiro") ?? DefaultRanged) + LevelBonus;
        public int MeleeDamageBonus => (_character is { } c ? c[Trait.Robur] / 2 : 1) + LevelBonus;
        public int RangedDamageBonus => (_character is { } c ? c[Trait.Celeritas] / 3 : 0) + LevelBonus;
        public const int MeleeDie = 8, RangedDie = 6;

        public int NextLevelAt => Level >= Progression.MaxLevel ? Progression.ExperienceFor(Progression.MaxLevel) : Progression.ExperienceFor(Level + 1);

        /// <summary>Aggiunge esperienza; restituisce true se il personaggio è salito di livello (e lo cura del tutto).</summary>
        internal bool Gain(int experience)
        {
            Experience += experience;
            int level = Progression.LevelFor(Experience);
            if (level == Level) return false;
            Level = level;
            Health = MaxHealth;
            return true;
        }
    }
}

// ===== src/LexArcana.Core/Combat/Progression.cs =====
namespace LexArcana.Combat
{
    /// <summary>Livelli ed esperienza dei personaggi.</summary>
    public static class Progression
    {
        public const int MaxLevel = 10;

        /// <summary>L'esperienza totale che serve per arrivare a un livello: 0, 100, 300, 600, 1000…</summary>
        public static int ExperienceFor(int level) => 50 * level * (level - 1);

        public static int LevelFor(int experience)
        {
            int level = 1;
            while (level < MaxLevel && experience >= ExperienceFor(level + 1)) level++;
            return level;
        }

        /// <summary>
        /// Il livello dei nemici di un luogo: cresce con l'avventura e con il livello medio del gruppo,
        /// così i combattimenti restano una sfida senza diventare impossibili.
        /// </summary>
        public static int EncounterLevel(int adventure, double partyLevel) =>
            Math.Clamp((int)Math.Round(Math.Max(1, adventure) + Math.Max(1, partyLevel) - 1), 1, MaxLevel);
    }
}

// ===== src/LexArcana.Core/Game/CharacterSheet.cs =====
namespace LexArcana.Game
{
    /// <summary>
    /// La scheda di un personaggio: campi liberi nome → valore (Virtù, Ferite, Equipaggiamento…).
    /// I campi restano nell'ordine in cui sono stati scritti; i nomi non distinguono maiuscole e minuscole.
    /// </summary>
    public sealed class CharacterSheet(string owner)
    {
        public const int MaxFields = 40;
        public const int MaxKeyLength = 32;
        public const int MaxValueLength = 200;

        private readonly OrderedDictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        public string Owner { get; } = owner;

        /// <summary>Imposta un campo; un valore vuoto lo rimuove.</summary>
        public bool TrySet(string? key, string? value, out string error)
        {
            error = "";
            key = key.Sanitized.Trim();
            value = value.Sanitized.Trim();
            if (key.Length is 0 or > MaxKeyLength)
            {
                error = $"Il nome del campo deve avere da 1 a {MaxKeyLength} caratteri.";
                return false;
            }
            if (value.Length > MaxValueLength)
            {
                error = $"Il valore può avere al massimo {MaxValueLength} caratteri.";
                return false;
            }

            lock (_lock)
            {
                if (value.Length == 0)
                {
                    _fields.Remove(key);
                    return true;
                }
                if (_fields.TryGetValue(key, out _, out int index))
                {
                    _fields.SetAt(index, value);
                    return true;
                }
                if (_fields.Count >= MaxFields)
                {
                    error = $"Una scheda ha al massimo {MaxFields} campi.";
                    return false;
                }
                _fields.Add(key, value);
                return true;
            }
        }

        public Dictionary<string, string> Snapshot()
        {
            lock (_lock) return new(_fields);
        }
    }
}

// ===== src/LexArcana.Core/Game/Dice.cs =====
namespace LexArcana.Game
{
    /// <summary>Un gruppo di dadi uguali: Count dadi da Sides facce, sommati (Sign = 1) o sottratti (Sign = -1).</summary>
    public readonly record struct DiceGroup(int Count, int Sides, int Sign);

    /// <summary>Un'espressione già controllata, pronta da tirare.</summary>
    public sealed record DiceExpression(string Text, DiceGroup[] Groups, int Modifier);

    /// <summary>Il risultato di un tiro.</summary>
    public sealed record DiceResult(DiceExpression Expression, int[] Dice)
    {
        public int Total => Dice.Sum() + Expression.Modifier;
    }

    /// <summary>
    /// Tira i dadi in notazione standard: termini <c>NdM</c> o numeri separati da + o -,
    /// per esempio <c>d20</c>, <c>3d6+2</c>, <c>1d8+1d4-1</c>.
    /// </summary>
    public sealed partial class Dice(Random? random = null)
    {
        public const int MaxDice = 100;
        public const int MaxSides = 1000;
        public const int MaxTerms = 10;
        public const int MaxModifier = 10_000;
        public const string Hint = "Scrivi un tiro come d20, 3d6+2 o 1d8+1d4.";

        private readonly Random _random = random ?? Random.Shared;
        private readonly Lock _lock = new();

        [GeneratedRegex(@"^(?<sign>[+-]?)(?:(?<count>\d{0,4})d(?<sides>\d{1,5})|(?<flat>\d{1,6}))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Term { get; }

        [GeneratedRegex("(?=[+-])")]
        private static partial Regex BeforeSign { get; }

        public static bool TryParse(string? input, out DiceExpression expression, out string error)
        {
            expression = null!;
            error = Hint;
            var text = (input ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            if (text.Length is 0 or > 64) return false;

            var terms = BeforeSign.Split(text).Where(t => t.Length > 0).ToArray();
            if (terms.Length > MaxTerms)
            {
                error = $"Al massimo {MaxTerms} termini per tiro.";
                return false;
            }

            List<DiceGroup> groups = [];
            int modifier = 0;
            foreach (var term in terms)
            {
                var m = Term.Match(term);
                if (!m.Success)
                {
                    error = $"«{term}» non è un dado valido. {Hint}";
                    return false;
                }

                int sign = m.Groups["sign"].Value == "-" ? -1 : 1;
                if (m.Groups["flat"].Success)
                {
                    modifier += sign * Number(m.Groups["flat"].Value);
                    continue;
                }

                var count = m.Groups["count"].Value is { Length: > 0 } c ? Number(c) : 1;
                var sides = Number(m.Groups["sides"].Value);
                if (count < 1 || groups.Sum(g => g.Count) + count > MaxDice)
                {
                    error = $"Da 1 a {MaxDice} dadi per tiro.";
                    return false;
                }
                if (sides is < 2 or > MaxSides)
                {
                    error = $"Un dado ha da 2 a {MaxSides} facce.";
                    return false;
                }
                groups.Add(new(count, sides, sign));
            }

            if (groups.Count == 0)
            {
                error = $"Il tiro deve contenere almeno un dado. {Hint}";
                return false;
            }
            if (Math.Abs(modifier) > MaxModifier)
            {
                error = "Modificatore troppo grande.";
                return false;
            }

            expression = new(text, [.. groups], modifier);
            return true;
        }

        public DiceResult Roll(DiceExpression expression)
        {
            List<int> dice = [];
            lock (_lock)
            {
                foreach (var (count, sides, sign) in expression.Groups)
                    for (int i = 0; i < count; i++)
                        dice.Add(sign * _random.Next(1, sides + 1));
            }
            return new(expression, [.. dice]);
        }

        /// <summary>Un dado da <paramref name="sides"/> facce.</summary>
        public int D(int sides)
        {
            lock (_lock) return _random.Next(1, sides + 1);
        }

        private static int Number(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);
    }
}

// ===== src/LexArcana.Core/Game/GameMap.cs =====
namespace LexArcana.Game
{
    /// <summary>Che cosa c'è in una casella della mappa.</summary>
    public enum Tile { Void, Floor, Wall, Column, Altar }

    /// <summary>Una posizione nel mondo, in metri; Yaw è la direzione dello sguardo in radianti (0 = verso +Z).</summary>
    public readonly record struct Pose(float X, float Z, float Yaw)
    {
        public bool IsFinite => float.IsFinite(X) && float.IsFinite(Z) && float.IsFinite(Yaw);
        public float DistanceTo(Pose other) => MathF.Sqrt((X - other.X) * (X - other.X) + (Z - other.Z) * (Z - other.Z));
    }

    /// <summary>
    /// Una mappa a caselle, scritta come testo. Ogni carattere è una casella di <see cref="CellSize"/> metri:
    /// <c>#</c> muro, <c>.</c> pavimento, <c>C</c> colonna, <c>A</c> altare, <c>S</c> punto di partenza
    /// (pavimento), spazio = vuoto. Una prima riga <c>nome: …</c> dà il nome al luogo.
    /// </summary>
    public sealed class GameMap
    {
        public const float CellSize = 2f;
        public const float WallHeight = 3.2f;
        public const float PlayerRadius = 0.35f;
        public const int MaxSize = 96;

        private readonly Tile[,] _tiles;

        private GameMap(string name, string[] rows, Tile[,] tiles, Pose[] spawns)
        {
            Name = name;
            Rows = rows;
            _tiles = tiles;
            Spawns = spawns;
        }

        public string Name { get; }
        /// <summary>Il testo della mappa, una stringa per riga: è ciò che viaggia in rete.</summary>
        public string[] Rows { get; }
        public int Width => _tiles.GetLength(0);
        public int Depth => _tiles.GetLength(1);
        public Pose[] Spawns { get; }

        public Tile this[int x, int z] => x >= 0 && z >= 0 && x < Width && z < Depth ? _tiles[x, z] : Tile.Void;

        public static bool IsSolid(Tile tile) => tile is Tile.Wall or Tile.Column or Tile.Altar or Tile.Void;

        /// <summary>Il centro della casella (x, z) in metri.</summary>
        public static (float X, float Z) CellCenter(int x, int z) => ((x + 0.5f) * CellSize, (z + 0.5f) * CellSize);

        /// <summary>True se un personaggio può stare in (x, z) senza toccare caselle solide.</summary>
        public bool IsWalkable(float x, float z, float radius = PlayerRadius)
        {
            if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
            int minX = (int)MathF.Floor((x - radius) / CellSize), maxX = (int)MathF.Floor((x + radius) / CellSize);
            int minZ = (int)MathF.Floor((z - radius) / CellSize), maxZ = (int)MathF.Floor((z + radius) / CellSize);
            for (int cx = minX; cx <= maxX; cx++)
                for (int cz = minZ; cz <= maxZ; cz++)
                    if (IsSolid(this[cx, cz]) && CircleTouchesCell(x, z, radius, cx, cz)) return false;
            return true;
        }

        /// <summary>
        /// Muove da <paramref name="from"/> di (dx, dz) scivolando lungo i muri: prima prova l'asse X, poi Z.
        /// </summary>
        public Pose Slide(Pose from, float dx, float dz, float yaw)
        {
            float x = from.X, z = from.Z;
            if (IsWalkable(x + dx, z)) x += dx;
            if (IsWalkable(x, z + dz)) z += dz;
            return new(x, z, yaw);
        }

        /// <summary>True se da (x0, z0) si vede (x1, z1) ad altezza d'occhi: nessun muro o colonna in mezzo. L'altare è basso e non copre.</summary>
        public bool HasLineOfSight(float x0, float z0, float x1, float z1)
        {
            float dx = x1 - x0, dz = z1 - z0;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            int steps = (int)MathF.Ceiling(length / (CellSize * 0.25f));
            for (int i = 1; i < steps; i++)
            {
                float t = (float)i / steps;
                var tile = this[(int)MathF.Floor((x0 + dx * t) / CellSize), (int)MathF.Floor((z0 + dz * t) / CellSize)];
                if (tile is Tile.Wall or Tile.Column or Tile.Void) return false;
            }
            return true;
        }

        private static bool CircleTouchesCell(float x, float z, float r, int cx, int cz)
        {
            float nearestX = Math.Clamp(x, cx * CellSize, (cx + 1) * CellSize);
            float nearestZ = Math.Clamp(z, cz * CellSize, (cz + 1) * CellSize);
            float dx = x - nearestX, dz = z - nearestZ;
            return dx * dx + dz * dz < r * r;
        }

        public static bool TryParse(IEnumerable<string> lines, out GameMap map, out string error)
        {
            map = null!;
            error = "";
            var name = "Luogo senza nome";
            List<string> rows = [];
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (rows.Count == 0 && line.StartsWith("nome:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line[5..].Sanitized.Trim() is { Length: > 0 } n ? n : name;
                    continue;
                }
                if (rows.Count == 0 && line.Trim().Length == 0) continue;
                rows.Add(line);
            }
            while (rows.Count > 0 && rows[^1].Trim().Length == 0) rows.RemoveAt(rows.Count - 1);

            if (rows.Count == 0) return Fail("La mappa è vuota.", out error);
            int width = rows.Max(r => r.Length);
            if (width > MaxSize || rows.Count > MaxSize) return Fail($"La mappa può essere al massimo {MaxSize}×{MaxSize} caselle.", out error);

            var tiles = new Tile[width, rows.Count];
            List<Pose> spawns = [];
            List<Pose> floors = [];
            for (int z = 0; z < rows.Count; z++)
                for (int x = 0; x < width; x++)
                {
                    char c = x < rows[z].Length ? rows[z][x] : ' ';
                    Tile? tile = c switch
                    {
                        '#' => Tile.Wall,
                        '.' or 'S' or 's' => Tile.Floor,
                        'C' or 'c' => Tile.Column,
                        'A' or 'a' => Tile.Altar,
                        ' ' => Tile.Void,
                        _ => null,
                    };
                    if (tile is not { } known)
                        return Fail($"Carattere «{c}» non valido alla riga {z + 1}, colonna {x + 1}. Usa # . C A S o spazio.", out error);
                    tiles[x, z] = known;
                    var (cxm, czm) = CellCenter(x, z);
                    if (c is 'S' or 's') spawns.Add(new(cxm, czm, 0));
                    if (tiles[x, z] == Tile.Floor) floors.Add(new(cxm, czm, 0));
                }

            if (floors.Count == 0) return Fail("La mappa non ha pavimento su cui stare.", out error);

            // Chi entra guarda verso il centro del pavimento.
            float centerX = floors.Average(f => f.X), centerZ = floors.Average(f => f.Z);
            Pose[] starts = [.. (spawns.Count > 0 ? spawns : [floors[0]])
                .Select(p => p with { Yaw = MathF.Atan2(centerX - p.X, centerZ - p.Z) })];
            map = new GameMap(name, [.. rows], tiles, starts);
            return true;

            static bool Fail(string message, out string error)
            {
                error = message;
                return false;
            }
        }

        /// <summary>La mappa usata quando il Demiurgo non ne sceglie una.</summary>
        public static GameMap Default { get; } = Parse(DefaultText);

        private static GameMap Parse(string text) =>
            TryParse(text.Split('\n'), out var map, out var error) ? map : throw new InvalidOperationException(error);

        public const string DefaultText = """
            nome: Tempio di Giano
            ###############
            #.............#
            #.C...C.C...C.#
            #.............#
            #......A......#
            #.............#
            #.C...C.C...C.#
            #.............#
            ######...######
                 #...#
                 #.S.#
                 #S.S#
                 #####
            """;
    }
}

// ===== src/LexArcana.Core/Game/Text.cs =====
namespace LexArcana.Game
{
    /// <summary>Regole sul testo condivise da server e client (membri di estensione di C# 14).</summary>
    public static class Text
    {
        public const int MaxMessageLength = 1000;

        extension(string? text)
        {
            /// <summary>Il testo senza caratteri di controllo: nessuno può inviare sequenze ANSI ai terminali degli altri.</summary>
            public string Sanitized => text is null ? "" : string.Concat(text.Where(c => !char.IsControl(c)));

            /// <summary>Il testo ripulito e accorciato, o null se è vuoto o supera <see cref="MaxMessageLength"/>.</summary>
            public string? AsMessage => text.Sanitized.Trim() is { Length: > 0 and <= MaxMessageLength } clean ? clean : null;

            public bool IsSameName(string? other) => string.Equals(text, other, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class Names
    {
        /// <summary>Il nome riservato a chi conduce la partita dal server.</summary>
        public const string Demiurge = "Demiurgo";
        public const int MaxLength = 24;

        /// <summary>Un nome valido ha 1–24 lettere, cifre, '_' o '-'.</summary>
        public static bool IsValid(string? name) =>
            name is { Length: > 0 and <= MaxLength } && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

        public static bool IsDemiurge(string? name) => name.IsSameName(Demiurge);
    }
}

// ===== src/LexArcana.Core/Net/CommandLine.cs =====
namespace LexArcana.Net
{
    /// <summary>Scompone le righe digitate nel terminale.</summary>
    public static class CommandLine
    {
        /// <summary>"/Comando resto del testo" → ("comando", "resto del testo").</summary>
        public static (string Command, string Remainder) Split(string line)
        {
            var (command, remainder) = FirstWord(line.TrimStart('/'));
            return (command.ToLowerInvariant(), remainder);
        }

        /// <summary>"parola resto del testo" → ("parola", "resto del testo"), mantenendo maiuscole e minuscole.</summary>
        public static (string First, string Remainder) FirstWord(string text)
        {
            text = text.Trim();
            int space = text.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
        }
    }
}

// ===== src/LexArcana.Core/Net/GameClient.cs =====
namespace LexArcana.Net
{
    /// <summary>
    /// Il client di un giocatore: si collega al server del Demiurgo, si presenta con un nome e
    /// riceve i messaggi della sessione con <see cref="ReadAllAsync"/>.
    /// </summary>
    public sealed class GameClient : IAsyncDisposable
    {
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);
        private readonly IConnection _connection;

        private GameClient(IConnection connection, Welcome welcome)
        {
            _connection = connection;
            Welcome = welcome;
        }

        public Welcome Welcome { get; }
        public string Name => Welcome.Name;

        /// <summary>Si collega via TCP e si presenta. Se il server rifiuta, lancia <see cref="ConnectionRefusedException"/> con il motivo.</summary>
        public static async Task<GameClient> ConnectAsync(string host, int port, string name, string? password = null, CancellationToken ct = default)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port, ct);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
            return await HelloAsync(new LineConnection(tcp), name, password, ct);
        }

        /// <summary>
        /// Si collega via WebSocket (es. <c>ws://host:8080/ws</c>) e si presenta: è la strada dei browser, iPhone compreso.
        /// </summary>
        public static async Task<GameClient> ConnectWebSocketAsync(Uri uri, string name, string? password = null, CancellationToken ct = default)
        {
            var socket = new System.Net.WebSockets.ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, ct);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return await HelloAsync(new WebSocketConnection(socket, uri.ToString()), name, password, ct);
        }

        /// <summary>
        /// Si collega a un tavolo: con un indirizzo web (<c>https://tavolo.esempio.it</c>, <c>wss://…/ws</c>, <c>http://…</c>)
        /// via WebSocket, come i browser; con un nome o un indirizzo IP via TCP sulla porta indicata.
        /// </summary>
        public static Task<GameClient> ConnectToAsync(string host, int port, string name, string? password = null, CancellationToken ct = default) =>
            WebSocketUri(host) is { } uri ? ConnectWebSocketAsync(uri, name, password, ct) : ConnectAsync(host, port, name, password, ct);

        /// <summary>L'indirizzo WebSocket di un tavolo online, o null se <paramref name="host"/> non è un indirizzo web.</summary>
        public static Uri? WebSocketUri(string host)
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var uri)) return null;
            string? scheme = uri.Scheme switch { "https" or "wss" => "wss", "http" or "ws" => "ws", _ => null };
            if (scheme is null) return null;
            var builder = new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port };
            if (builder.Path is "" or "/") builder.Path = "/ws";
            return builder.Uri;
        }

        private static async Task<GameClient> HelloAsync(IConnection connection, string name, string? password, CancellationToken ct)
        {
            try
            {
                connection.Post(Wire.Encode(new Hello(name, string.IsNullOrEmpty(password) ? null : password)));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HelloTimeout);
                var reply = await connection.ReadLineAsync(timeout.Token) is { } line
                    ? Wire.DecodeServer(line)
                    : throw new ConnectionRefusedException("Il server ha chiuso la connessione.");
                return reply switch
                {
                    Welcome welcome => new GameClient(connection, welcome),
                    Refused(var reason) => throw new ConnectionRefusedException(reason),
                    _ => throw new ConnectionRefusedException("Risposta inattesa dal server."),
                };
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>Mette in coda un messaggio per il server; false se la connessione è chiusa.</summary>
        public bool Send(ClientMessage message) => _connection.Post(Wire.Encode(message));

        /// <summary>
        /// I messaggi del server, finché la connessione resta aperta. Termina normalmente quando
        /// il server chiude; lancia <see cref="ProtocolException"/> se riceve qualcosa di illeggibile.
        /// </summary>
        public async IAsyncEnumerable<ServerMessage> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            while (true)
            {
                string? line;
                try { line = await _connection.ReadLineAsync(ct); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or System.Net.WebSockets.WebSocketException) { yield break; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { yield break; } // chiusa da noi
                if (line is null) yield break;
                yield return Wire.DecodeServer(line);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Send(new Leave());
            await _connection.FlushAsync(TimeSpan.FromSeconds(1));
            await _connection.DisposeAsync();
        }
    }

    public sealed class ConnectionRefusedException(string message) : Exception(message);
}

// ===== src/LexArcana.Core/Net/GameServer.cs =====
namespace LexArcana.Net
{
    /// <summary>
    /// Il server della sessione, condotto dal Demiurgo. Accetta i giocatori via TCP, tira tutti i dadi
    /// (così nessun client può barare), conserva le schede e inoltra i messaggi.
    /// Il Demiurgo non è un client: vede ogni messaggio tramite <c>demiurgeView</c>, compresi
    /// sussurri e tiri segreti, e agisce con <see cref="ExecuteAsync"/>.
    /// </summary>
    public sealed class GameServer(IPAddress address, int port, Action<ServerMessage> demiurgeView, string? password = null, Dice? dice = null, GameMap? map = null, Campaign? campaign = null, bool peaceful = false, string? demiurgePassword = null)
        : IAsyncDisposable
    {
        public const int DefaultPort = 7777;
        public const int MaxPlayers = 16;
        /// <summary>Lo spostamento massimo accettato tra due messaggi Move, in metri (copre qualche pacchetto in ritardo).</summary>
        public const float MaxStep = 3f;
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

        private readonly TcpListener _listener = new(address, port);
        private readonly Dice _dice = dice ?? new Dice();
        private readonly byte[]? _password = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
        private readonly byte[]? _demiurgePassword = string.IsNullOrEmpty(demiurgePassword) ? null : Encoding.UTF8.GetBytes(demiurgePassword);

        /// <summary>
        /// Chi conduce da remoto (es. un tavolo online, dove nessuno siede davanti alla console del server):
        /// riceve tutto ciò che vede la console e può dare i comandi del Demiurgo.
        /// </summary>
        private readonly ConcurrentDictionary<IConnection, byte> _remoteDemiurges = new();

        /// <summary>Ciò che vede il Demiurgo: la console del server e chi conduce da remoto.</summary>
        private void View(ServerMessage message)
        {
            demiurgeView(message);
            if (_remoteDemiurges.IsEmpty) return;
            var line = Wire.Encode(message);
            foreach (var remote in _remoteDemiurges.Keys) remote.Post(line);
        }
        private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CharacterSheet> _sheets = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Pose> _poses = new(StringComparer.OrdinalIgnoreCase);
        private int _nextSpawn;

        // Combattimento: il motore non è sicuro tra thread, quindi lo si tocca solo sotto questo lock.
        private readonly Lock _combatLock = new();
        private CombatEngine? _combatEngine;
        private CombatEngine Combat => _combatEngine ??= new CombatEngine(_dice, Map);
        /// <summary>I giocatori che si muovono nella scena 3D: solo loro possono essere visti e attaccati dai nemici.</summary>
        private readonly ConcurrentDictionary<string, byte> _inWorld = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _random = new();
        private Task? _ticker;
        public const int TicksPerSecond = 10;
        private double Now => _clock.Elapsed.TotalSeconds;
        private readonly ConcurrentDictionary<Task, byte> _sessions = new();
        private readonly CancellationTokenSource _stopping = new();
        private Task? _acceptLoop;

        private sealed record Player(string Name, IConnection Connection)
        {
            /// <summary>
            /// Diventa true appena il benvenuto è in coda: da lì in poi riceve anche i messaggi diffusi a tutti.
            /// Prima, un messaggio diffuso potrebbe arrivargli prima del benvenuto e il client lo rifiuterebbe.
            /// </summary>
            public volatile bool Ready;

            public bool Send(ServerMessage message) => Connection.Post(Wire.Encode(message));
        }

        /// <summary>La porta effettiva (utile se si è chiesta la porta 0).</summary>
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string[] PlayerNames => [.. _players.Keys.Order(StringComparer.OrdinalIgnoreCase)];

        private volatile GameMap _map = map ?? GameMap.Default;

        /// <summary>Il luogo in cui si trova il gruppo; il Demiurgo lo cambia con /avventura.</summary>
        public GameMap Map => _map;

        /// <summary>La campagna caricata, se c'è, e l'avventura in corso (da 1).</summary>
        public Campaign? Campaign { get; } = campaign;
        public int AdventureNumber { get; private set; }

        /// <summary>
        /// Porta tutti in un altro luogo: i giocatori ripartono dai punti di partenza della nuova mappa
        /// e i client 3D ricostruiscono la scena.
        /// </summary>
        public void ChangeMap(GameMap newMap, string? title = null, PlaceKind? kind = null, int adventure = 1, bool final = false, string? boss = null)
        {
            _map = newMap;
            int i = 0;
            foreach (var name in PlayerNames) _poses[name] = newMap.Spawns[i++ % newMap.Spawns.Length];
            EnemyList enemies;
            lock (_combatLock)
            {
                Combat.Populate(newMap, kind, adventure, final, boss, _random);
                if (peaceful) Combat.Clear();
                enemies = new EnemyList([.. Combat.Enemies.Select(Info)]);
            }
            Broadcast(new MapChanged(new MapData(newMap.Name, newMap.Rows), Positions(), title));
            Broadcast(enemies);
        }

        /// <summary>Comincia un'avventura della campagna: cambia luogo e narra il gancio a tutti.</summary>
        public ServerMessage StartAdventure(int number)
        {
            if (Campaign is null) return new Refused("Nessuna campagna caricata: avvia il server con --campagna <file>.");
            if (number < 1 || number > Campaign.Adventures.Length) return new Refused($"La campagna ha le avventure da 1 a {Campaign.Adventures.Length}.");
            var adventure = Campaign.Adventures[number - 1];
            if (!GameMap.TryParse([$"nome: {adventure.Place}", .. adventure.Map], out var adventureMap, out var error))
                return new Refused($"La mappa dell'avventura {number} non è valida: {error}");
            AdventureNumber = number;
            var title = $"{number}. {adventure.Title}";
            bool final = number == Campaign.Adventures.Length;
            ChangeMap(adventureMap, title, adventure.Kind, number, final, final ? Campaign.Villain.Split(',')[0] : null);
            Broadcast(new Narrated($"{title}. {adventure.Hook}"));
            return AdventureBrief(adventure);
        }

        /// <summary>Il promemoria privato del Demiurgo per un'avventura: obiettivo, PNG, complicazione, climax.</summary>
        private static Said AdventureBrief(Adventure a)
        {
            var npcs = string.Join(" · ", a.Npcs.Select(n => $"{n.Name}, {n.Role} ({n.Trait}; segreto: {n.Secret})"));
            return new Said(Names.Demiurge,
                $"[solo per te] Obiettivo: {a.Objective} PNG: {npcs}. Complicazione: {a.Complication}. Climax: {a.Climax} Ricompensa: {a.Reward}.");
        }

        public Pose? PoseOf(string name) => _poses.TryGetValue(name, out var pose) ? pose : null;

        private void Track(Task session)
        {
            _sessions.TryAdd(session, 0);
            _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }

        private WebGateway? _web;
        private Task? _webLoop;

        /// <summary>
        /// Apre anche la porta web: WebSocket su /ws per i giocatori nel browser (iPhone compreso) e,
        /// se c'è <paramref name="webRoot"/>, i file del client web. Va chiamato dopo <see cref="Start"/>.
        /// </summary>
        public int StartWeb(IPAddress webAddress, int webPort, string? webRoot)
        {
            _web = new WebGateway(webAddress, webPort, (connection, ct) =>
            {
                var session = RunSessionAsync(connection, ct);
                Track(session);
                return session;
            }, webRoot);
            _webLoop = _web.StartAsync(_stopping.Token);
            return _web.Port;
        }

        public int? WebPort => _web?.Port;

        public void Start()
        {
            _listener.Start();
            if (AdventureNumber == 0 && !peaceful) lock (_combatLock) Combat.Populate(Map, null, 1, false, null, _random);
            _acceptLoop = AcceptLoopAsync(_stopping.Token);
            _ticker = TickLoopAsync(_stopping.Token);
        }

        /// <summary>Quanti nemici ci sono ora (per i test e per /nemici).</summary>
        public IReadOnlyList<EnemyInfo> EnemySnapshot()
        {
            lock (_combatLock) return [.. Combat.Enemies.Select(Info)];
        }

        public Vitals? VitalsOf(string name)
        {
            lock (_combatLock) return Combat.FighterOf(name) is { } f ? ToVitals(f) : null;
        }

        private async Task TickLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / TicksPerSecond));
            double last = Now;
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    double now = Now;
                    var players = _inWorld.Keys.Where(_poses.ContainsKey).ToDictionary(n => n, n => _poses[n], StringComparer.OrdinalIgnoreCase);
                    List<(ServerMessage, string?)> messages;
                    lock (_combatLock) messages = Translate(Combat.Tick(now, (float)Math.Min(now - last, 0.25), players));
                    last = now;
                    Publish(messages);
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Traduce gli eventi del motore in messaggi: ogni coppia è (messaggio, destinatario); destinatario null = tutti.
        /// Va chiamato sotto <see cref="_combatLock"/>, perché legge lo stato di nemici e combattenti.
        /// </summary>
        private List<(ServerMessage Message, string? To)> Translate(IEnumerable<CombatEvent> events)
        {
            List<(ServerMessage, string?)> messages = [];
            foreach (var e in events)
                switch (e)
                {
                    case EnemiesChanged:
                        messages.Add((new EnemyList([.. Combat.Enemies.Select(Info)]), null));
                        break;
                    case EnemiesMoved(var enemies):
                        messages.Add((new EnemyMoves([.. enemies.Select(x => new EnemyPose(x.Id, x.Pose.X, x.Pose.Z, x.Pose.Yaw))]), null));
                        break;
                    case Blow b:
                        messages.Add((new Struck(b.Attacker, b.Target, b.Roll, b.Total, b.Against, b.Hit, b.Critical, b.Damage,
                            b.Health, b.MaxHealth, b.Defeated, b.Ranged, b.AttackerEnemyId, b.TargetEnemyId), null));
                        break;
                    case VitalsChanged(var f):
                        messages.Add((ToVitals(f), f.Name));
                        break;
                    case LevelUp(var f):
                        messages.Add((new Narrated($"{f.Name} sale al livello {f.Level}: salute piena, colpi più sicuri."), null));
                        break;
                    case Respawned(var f, var pose):
                        _poses[f.Name] = pose;
                        messages.Add((new Moved(f.Name, pose.X, pose.Z, pose.Yaw), null));
                        break;
                }
            return messages;
        }

        private void Publish(List<(ServerMessage Message, string? To)> messages)
        {
            foreach (var (message, to) in messages)
            {
                if (to is null) Broadcast(message);
                else if (_players.TryGetValue(to, out var player) && player.Ready) player.Send(message);
            }
        }

        private static EnemyInfo Info(Enemy e) =>
            new(e.Id, e.Kind.Id, e.Name, e.Level, e.Health, e.MaxHealth, e.Pose.X, e.Pose.Z, e.Pose.Yaw, e.Rank.ToString());

        private static Vitals ToVitals(Fighter f) =>
            new(f.Name, f.Health, f.MaxHealth, f.Level, f.Experience, f.NextLevelAt, f.IsDown);

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(ct); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
                catch (SocketException) { continue; }

                Track(RunSessionAsync(new LineConnection(tcp), ct));
            }
        }

        private async Task RunSessionAsync(IConnection connection, CancellationToken ct)
        {
            Player? player = null;
            try
            {
                player = await HandshakeAsync(connection, ct);
                if (player is null) return;

                while (await connection.ReadLineAsync(ct) is { } line)
                {
                    var message = Wire.DecodeClient(line);
                    if (message is Leave) break;
                    if (Handle(player, message) is { } reply) player.Send(reply);
                }
            }
            catch (ProtocolException ex)
            {
                connection.Post(Wire.Encode(new Refused(ex.Message)));
                await connection.FlushAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                if (player is not null && _players.TryRemove(new(player.Name, player)))
                {
                    _poses.TryRemove(player.Name, out _);
                    _inWorld.TryRemove(player.Name, out _);
                    lock (_combatLock) Combat.Leave(player.Name);
                    Broadcast(new Left(player.Name));
                }
                await connection.DisposeAsync();
            }
        }

        private async Task<Player?> HandshakeAsync(IConnection connection, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HelloTimeout);
            if (await connection.ReadLineAsync(timeout.Token) is not { } line) return null;
            var first = Wire.DecodeClient(line);

            if (first is Hello { Version: Wire.ProtocolVersion } dh && Names.IsDemiurge(dh.Name) && _demiurgePassword is not null &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(dh.Password ?? ""), _demiurgePassword))
            {
                await RunRemoteDemiurgeAsync(connection, ct);
                return null;
            }

            var player = first switch
            {
                not Hello => Refuse("Il primo messaggio deve essere hello."),
                Hello { Version: not Wire.ProtocolVersion } => Refuse($"Versione del protocollo non supportata: il server usa la {Wire.ProtocolVersion}. Aggiorna il client."),
                Hello h when !PasswordMatches(h.Password) => Refuse("Parola d'ordine errata."),
                Hello h when !Names.IsValid(h.Name) => Refuse($"Scegli un nome di 1–{Names.MaxLength} lettere o cifre, senza spazi."),
                Hello h when Names.IsDemiurge(h.Name) => Refuse(_demiurgePassword is null
                    ? $"Il nome «{Names.Demiurge}» è riservato a chi conduce la partita."
                    : "Parola d'ordine del Demiurgo errata."),
                Hello when _players.Count >= MaxPlayers => Refuse("La sessione è al completo."),
                Hello h when !_players.TryAdd(h.Name, new(h.Name, connection)) => Refuse($"Il nome «{h.Name}» è già in uso."),
                Hello h => _players[h.Name],
            };
            if (player is null)
            {
                await connection.FlushAsync(TimeSpan.FromSeconds(1));
                return null;
            }

            SheetOf(player.Name);
            Vitals vitals;
            EnemyList enemyList;
            lock (_combatLock)
            {
                vitals = ToVitals(Combat.Join(player.Name));
                enemyList = new EnemyList([.. Combat.Enemies.Select(Info)]);
            }
            var spawn = Map.Spawns[(int)((uint)Interlocked.Increment(ref _nextSpawn) % Map.Spawns.Length)];
            _poses[player.Name] = spawn;
            player.Send(new Welcome(player.Name, Present(), new MapData(Map.Name, Map.Rows), Positions()));
            player.Ready = true;
            Broadcast(new Joined(player.Name), except: player.Name);
            Broadcast(new Moved(player.Name, spawn.X, spawn.Z, spawn.Yaw), except: player.Name);
            player.Send(enemyList);
            player.Send(vitals);
            return player;

            Player? Refuse(string reason)
            {
                connection.Post(Wire.Encode(new Refused(reason)));
                return null;
            }
        }

        /// <summary>La sessione di chi conduce da remoto: ogni riga che manda è un comando del Demiurgo (o testo da dire a tutti).</summary>
        private async Task RunRemoteDemiurgeAsync(IConnection connection, CancellationToken ct)
        {
            connection.Post(Wire.Encode(new Welcome(Names.Demiurge, Present(), new MapData(Map.Name, Map.Rows), Positions())));
            lock (_combatLock) connection.Post(Wire.Encode(new EnemyList([.. Combat.Enemies.Select(Info)])));
            _remoteDemiurges.TryAdd(connection, 0);
            demiurgeView(new Narrated("Il Demiurgo conduce da remoto."));
            try
            {
                while (await connection.ReadLineAsync(ct) is { } line)
                {
                    var message = Wire.DecodeClient(line);
                    if (message is Leave) break;
                    var text = message switch { Command(var l) => l, Say(var t) => t, _ => null };
                    ServerMessage? reply = text is null ? new Refused("Da remoto il Demiurgo manda solo comandi e testo.")
                        : text.Trim() is "/fine" ? new Refused("La sessione si chiude solo dalla console del server.")
                        : await ExecuteAsync(text);
                    if (reply is not null) connection.Post(Wire.Encode(reply));
                }
            }
            finally
            {
                _remoteDemiurges.TryRemove(connection, out _);
            }
        }

        private bool PasswordMatches(string? given) =>
            _password is null || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given ?? ""), _password);

        /// <summary>Un messaggio da un giocatore; restituisce la risposta solo per lui, se c'è.</summary>
        private ServerMessage? Handle(Player player, ClientMessage message) => message switch
        {
            Say(var text) => Chat(player.Name, text),
            RollDice(var expression, var secret) => RollFor(player.Name, expression, secret),
            Whisper(var to, var text) => WhisperFrom(player.Name, to, text),
            SetField(var key, var value) => SetSheetField(player.Name, player.Name, key, value),
            SetSheet(var fields) => SetSheetFields(player.Name, fields),
            Embody(var character) => EmbodyAs(player, character),
            Attack(var enemyId, var ranged) => AttackWith(player, enemyId, ranged),
            ShowSheet(var owner) => Sheet(owner ?? player.Name),
            ListPlayers => new PlayerList(Present()),
            Move(var x, var z, var yaw) => MoveTo(player, new Pose(x, z, yaw)),
            Hello => new Refused("Sei già nella sessione."),
            _ => new Refused("Messaggio non previsto."),
        };

        // ---- Azioni comuni a giocatori e Demiurgo

        private Refused? Chat(string from, string? text)
        {
            if (text.AsMessage is not { } clean) return new("Il messaggio è vuoto o troppo lungo.");
            Broadcast(new Said(from, clean));
            return null;
        }

        private ServerMessage? RollFor(string from, string? expression, bool secret)
        {
            if (!Dice.TryParse(expression, out var parsed, out var error)) return new Refused(error);
            var result = _dice.Roll(parsed);
            var rolled = new Rolled(from, parsed.Text, result.Dice, parsed.Modifier, result.Total, secret);
            if (!secret)
            {
                Broadcast(rolled);
                return null;
            }
            View(rolled); // un tiro segreto lo vedono solo chi tira e il Demiurgo
            return Names.IsDemiurge(from) ? null : rolled;
        }

        private Refused? WhisperFrom(string from, string? to, string? text)
        {
            if (text.AsMessage is not { } clean) return new("Il messaggio è vuoto o troppo lungo.");
            if (string.IsNullOrWhiteSpace(to)) return new("A chi vuoi sussurrare?");
            if (from.IsSameName(to)) return new("Non puoi sussurrare a te stesso.");

            Player? target = null;
            if (!Names.IsDemiurge(to) && !_players.TryGetValue(to, out target)) return new($"«{to}» non è nella sessione.");

            var whisper = new Whispered(from, target?.Name ?? Names.Demiurge, clean);
            View(whisper); // il Demiurgo sente ogni sussurro
            target?.Send(whisper);
            if (!Names.IsDemiurge(from) && _players.TryGetValue(from, out var sender)) sender.Send(whisper);
            return null;
        }

        private ServerMessage SetSheetField(string actor, string owner, string? key, string? value)
        {
            var sheet = SheetOf(owner);
            if (!sheet.TrySet(key, value, out var error)) return new Refused(error);
            var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), actor);
            if (!Names.IsDemiurge(actor)) View(updated);
            return updated;
        }

        private ServerMessage SetSheetFields(string owner, Dictionary<string, string> fields)
        {
            var sheet = SheetOf(owner);
            if (fields.Count > CharacterSheet.MaxFields) return new Refused($"Una scheda ha al massimo {CharacterSheet.MaxFields} campi.");
            foreach (var (key, value) in fields)
                if (!sheet.TrySet(key, value, out var error)) return new Refused($"{key.Sanitized}: {error}");
            var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), owner);
            View(updated);
            return updated;
        }

        /// <summary>Entra in gioco con un personaggio: lo controlla, compila la scheda e prepara il combattimento.</summary>
        private ServerMessage EmbodyAs(Player player, Characters.Character character)
        {
            var problems = character.Validate();
            if (problems.Count > 0) return new Refused("Il personaggio non rispetta le regole: " + string.Join(" ", problems));
            var sheetReply = SetSheetFields(player.Name, character.ToSheet().ToDictionary(f => f.Key, f => f.Value));
            Vitals vitals;
            lock (_combatLock) vitals = ToVitals(Combat.Join(player.Name, character));
            player.Send(vitals);
            return sheetReply;
        }

        private Refused? AttackWith(Player player, int enemyId, bool ranged)
        {
            if (!_poses.TryGetValue(player.Name, out var pose)) return new Refused("Non sei nella scena.");
            List<(ServerMessage, string?)> messages;
            string? refusal;
            lock (_combatLock) messages = Translate(Combat.Attack(player.Name, pose, enemyId, ranged, Now, out refusal));
            Publish(messages);
            if (messages.Any(m => m.Item1 is Struck { Defeated: true, TargetEnemyId: not null }))
                lock (_combatLock) if (Combat.Enemies.Count == 0) Broadcast(new Narrated("Il luogo è libero: non resta nessun nemico."));
            return refusal is null ? null : new Refused(refusal);
        }

        private ServerMessage Sheet(string owner) =>
            _sheets.TryGetValue(owner, out var sheet)
                ? new SheetShown(sheet.Owner, sheet.Snapshot())
                : new Refused($"Non esiste una scheda di «{owner}».");

        /// <summary>
        /// Accetta una mossa se è un numero valido, non attraversa muri e non è un salto troppo lungo;
        /// altrimenti rimanda al giocatore la sua ultima posizione valida.
        /// </summary>
        private Moved? MoveTo(Player player, Pose wanted)
        {
            var current = _poses.GetValueOrDefault(player.Name, Map.Spawns[0]);
            bool down;
            lock (_combatLock) down = Combat.FighterOf(player.Name)?.IsDown == true;
            if (down) return new Moved(player.Name, current.X, current.Z, current.Yaw); // a terra non ci si muove
            if (!wanted.IsFinite || wanted.DistanceTo(current) > MaxStep || !Map.IsWalkable(wanted.X, wanted.Z))
                return new Moved(player.Name, current.X, current.Z, current.Yaw);

            var pose = wanted with { Yaw = NormalizeAngle(wanted.Yaw) };
            _poses[player.Name] = pose;
            _inWorld.TryAdd(player.Name, 0);
            var moved = Wire.Encode(new Moved(player.Name, pose.X, pose.Z, pose.Yaw));
            foreach (var other in _players.Values)
                if (other != player && other.Ready) other.Connection.Post(moved);
            return null;
        }

        private static float NormalizeAngle(float radians)
        {
            var a = radians % MathF.Tau;
            return a < 0 ? a + MathF.Tau : a;
        }

        private Position[] Positions() =>
            [.. _players.Values.Select(p => (p.Name, Pose: _poses.GetValueOrDefault(p.Name)))
                .Select(e => new Position(e.Name, e.Pose.X, e.Pose.Z, e.Pose.Yaw))];

        private CharacterSheet SheetOf(string name) => _sheets.GetOrAdd(name, static n => new CharacterSheet(n));

        // ---- Comandi del Demiurgo

        /// <summary>
        /// Esegue una riga digitata dal Demiurgo e restituisce cosa mostrargli in risposta, se c'è.
        /// Testo libero = parlare a tutti; i comandi iniziano con '/'.
        /// </summary>
        public async Task<ServerMessage?> ExecuteAsync(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return null;
            if (line[0] != '/') return Chat(Names.Demiurge, line);

            var (command, rest) = CommandLine.Split(line);
            switch (command)
            {
                case "narra":
                    if (rest.AsMessage is not { } narration) return new Refused("Uso: /narra <testo>");
                    Broadcast(new Narrated(narration));
                    return null;
                case "tira":
                    return RollFor(Names.Demiurge, rest, secret: false);
                case "segreto":
                    return RollFor(Names.Demiurge, rest, secret: true);
                case "sussurra":
                    var (to, text) = CommandLine.FirstWord(rest);
                    return WhisperFrom(Names.Demiurge, to, text);
                case "scheda":
                    return rest.Length == 0 ? new Refused("Uso: /scheda <giocatore>") : Sheet(rest);
                case "imposta":
                    var (owner, field) = CommandLine.FirstWord(rest);
                    var (key, value) = CommandLine.FirstWord(field);
                    if (key.Length == 0) return new Refused("Uso: /imposta <giocatore> <campo> [valore]");
                    if (!_sheets.ContainsKey(owner)) return new Refused($"Non esiste una scheda di «{owner}».");
                    var updated = SetSheetField(Names.Demiurge, owner, key, value);
                    if (updated is SheetShown && _players.TryGetValue(owner, out var player)) player.Send(updated);
                    return updated;
                case "chi":
                    return new PlayerList(Present());
                case "aiuto":
                    return new Said(Names.Demiurge, "[solo per te] Comandi: <testo> parla a tutti · /narra · /tira · /segreto · /sussurra <nome> · /scheda <nome> · " +
                        "/imposta <nome> <campo> [valore] · /chi · /espelli <nome> · /avventura [n] · /mappa <file> · /nemici · /evoca <tipo> [livello] [capo] · /pace · /cura [nome]");
                case "avventura":
                    if (rest.Length == 0) return AdventureList();
                    return int.TryParse(rest, out var number) ? StartAdventure(number) : new Refused("Uso: /avventura <numero>");
                case "nemici":
                {
                    var list = EnemySnapshot();
                    return new Said(Names.Demiurge, list.Count == 0
                        ? "[solo per te] Nessun nemico nel luogo. /evoca <tipo> [livello] per crearne; tipi: " + string.Join(", ", Bestiary.All.Select(k => k.Id)) + "."
                        : "[solo per te] " + string.Join(" · ", list.Select(e => $"#{e.Id} {e.Name} liv. {e.Level} ({e.Health}/{e.MaxHealth})")));
                }
                case "evoca":
                {
                    var (kindName, levelText) = CommandLine.FirstWord(rest);
                    if (Bestiary.Find(kindName) is not { } kind)
                        return new Refused("Uso: /evoca <tipo> [livello] [capo]. Tipi: " + string.Join(", ", Bestiary.All.Select(k => k.Id)) + ".");
                    var (lvText, rankText) = CommandLine.FirstWord(levelText);
                    EnemyList list;
                    lock (_combatLock)
                    {
                        int level = int.TryParse(lvText, out var l) ? l : Progression.EncounterLevel(Math.Max(1, AdventureNumber), Combat.PartyLevel);
                        var rank = rankText.Equals("capo", StringComparison.OrdinalIgnoreCase) ? EnemyRank.Elite : EnemyRank.Normal;
                        Combat.Summon(kind, level, rank, SummoningSpot());
                        list = new EnemyList([.. Combat.Enemies.Select(Info)]);
                    }
                    Broadcast(list);
                    return null;
                }
                case "pace":
                    lock (_combatLock) Combat.Clear();
                    Broadcast(new EnemyList([]));
                    Broadcast(new Narrated("Cala il silenzio: i nemici svaniscono."));
                    return null;
                case "cura":
                {
                    List<(ServerMessage, string?)> messages = [];
                    lock (_combatLock)
                        foreach (var f in Combat.Fighters.Where(f => rest.Length == 0 || f.Name.IsSameName(rest)))
                        {
                            f.Health = f.MaxHealth;
                            messages.Add((ToVitals(f), f.Name));
                        }
                    if (messages.Count == 0) return new Refused($"«{rest}» non è nella sessione.");
                    Publish(messages);
                    Broadcast(new Narrated(rest.Length == 0 ? "Una luce calda guarisce le ferite del gruppo." : $"Una luce calda guarisce le ferite di {rest}."));
                    return null;
                }
                case "mappa":
                    if (rest.Length == 0) return new Refused("Uso: /mappa <file>");
                    string[] lines;
                    try { lines = await File.ReadAllLinesAsync(rest); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Refused($"Impossibile leggere {rest}: {ex.Message}"); }
                    if (!GameMap.TryParse(lines, out var loaded, out var mapError)) return new Refused($"Mappa non valida: {mapError}");
                    ChangeMap(loaded, loaded.Name);
                    return null;
                case "espelli":
                    var (name, reason) = CommandLine.FirstWord(rest);
                    if (!_players.TryGetValue(name, out var expelled)) return new Refused($"«{name}» non è nella sessione.");
                    expelled.Send(new Expelled(reason.AsMessage));
                    await expelled.Connection.FlushAsync(TimeSpan.FromSeconds(1));
                    expelled.Connection.Close();
                    return null;
                default:
                    return new Refused($"Comando sconosciuto: /{command}. Scrivi /aiuto.");
            }
        }

        /// <summary>
        /// Dove far comparire un nemico evocato: una casella libera a 6–14 m da un giocatore e in vista,
        /// così il gruppo lo vede arrivare. Senza giocatori, o se non c'è un punto così, un pavimento qualsiasi.
        /// </summary>
        private Pose SummoningSpot()
        {
            var map = Map;
            var players = _poses.Values.ToList();
            Pose? fallback = null;
            for (int tries = 0; tries < 400; tries++)
            {
                int x = _random.Next(map.Width), z = _random.Next(map.Depth);
                if (map[x, z] != Tile.Floor) continue;
                var (cx, cz) = GameMap.CellCenter(x, z);
                fallback ??= new Pose(cx, cz, 0);
                foreach (var p in players)
                {
                    float d = MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz));
                    if (d is >= 6 and <= 14 && map.HasLineOfSight(p.X, p.Z, cx, cz))
                        return new Pose(cx, cz, MathF.Atan2(p.X - cx, p.Z - cz));
                }
            }
            return fallback ?? map.Spawns[0];
        }

        private ServerMessage AdventureList()
        {
            if (Campaign is null) return new Refused("Nessuna campagna caricata: avvia il server con --campagna <file>.");
            var list = string.Join(" · ", Campaign.Adventures.Select(a =>
                $"{(a.Number == AdventureNumber ? "▶ " : "")}{a.Number}. {a.Title} ({a.Place})"));
            return new Said(Names.Demiurge, $"[solo per te] {Campaign.Title}: {list}. Scrivi /avventura <numero> per portarci il gruppo.");
        }

        // ---- Utilità

        private string[] Present() => [Names.Demiurge, .. PlayerNames.Select(n => _players.TryGetValue(n, out var p) ? p.Name : n)];

        private void Broadcast(ServerMessage message, string? except = null)
        {
            View(message);
            var line = Wire.Encode(message);
            foreach (var player in _players.Values)
                if (player.Ready && !player.Name.IsSameName(except))
                    player.Connection.Post(line);
        }

        public async ValueTask DisposeAsync()
        {
            var closing = Wire.Encode(new Expelled("Il Demiurgo ha chiuso la sessione."));
            foreach (var player in _players.Values) player.Connection.Post(closing);
            await Task.WhenAll(_players.Values.Select(p => p.Connection.FlushAsync(TimeSpan.FromSeconds(1))));
            await _stopping.CancelAsync();
            _listener.Stop();
            _web?.Stop();
            if (_webLoop is not null) await _webLoop;
            _web?.Dispose();
            if (_ticker is not null) await _ticker;
            foreach (var player in _players.Values) player.Connection.Close();
            if (_acceptLoop is not null) await _acceptLoop;
            await Task.WhenAll(_sessions.Keys);
            _stopping.Dispose();
        }
    }
}

// ===== src/LexArcana.Core/Net/IConnection.cs =====
namespace LexArcana.Net
{
    /// <summary>
    /// Un canale di messaggi testuali verso un client: una riga JSON per messaggio.
    /// Lo implementano la connessione TCP (<see cref="LineConnection"/>) e quella WebSocket dei browser (<see cref="WebSocketConnection"/>).
    /// </summary>
    public interface IConnection : IAsyncDisposable
    {
        string RemoteEndPoint { get; }
        /// <summary>Si attiva quando la connessione si chiude, da qualunque lato.</summary>
        CancellationToken Closed { get; }
        /// <summary>Mette in coda un messaggio; false se la connessione è chiusa o l'altro capo non legge più.</summary>
        bool Post(string line);
        /// <summary>Attende che i messaggi in coda siano partiti (o che la connessione si chiuda).</summary>
        Task FlushAsync(TimeSpan timeout);
        /// <summary>Il prossimo messaggio, o null quando l'altro capo chiude.</summary>
        Task<string?> ReadLineAsync(CancellationToken ct);
        void Close();
    }
}

// ===== src/LexArcana.Core/Net/LineConnection.cs =====
namespace LexArcana.Net
{
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
}

// ===== src/LexArcana.Core/Net/PlayerCommands.cs =====
namespace LexArcana.Net
{
    /// <summary>Che cosa fare con una riga digitata da un giocatore.</summary>
    public abstract record PlayerInput
    {
        public sealed record Send(ClientMessage Message) : PlayerInput;
        public sealed record ShowHelp : PlayerInput;
        public sealed record Quit : PlayerInput;
        public sealed record Invalid(string Reason) : PlayerInput;
        public sealed record Empty : PlayerInput;
    }

    public static class PlayerCommands
    {
        public const string Help = """
            Comandi del giocatore
              <testo>                      parla a tutta la compagnia
              /tira <dadi>                 tira i dadi davanti a tutti, es. /tira 3d6+2
              /segreto <dadi>              tira di nascosto: lo vedete solo tu e il Demiurgo
              /sussurra <nome> <testo>     parla in privato (il Demiurgo sente tutto)
              /scheda [nome]               mostra la tua scheda o quella di un compagno
              /imposta <campo> [valore]    scrivi un campo della tua scheda; senza valore lo cancelli
              /chi                         chi è presente
              /aiuto                       questo elenco
              /esci                        lascia la sessione
            """;

        public static PlayerInput Parse(string? line)
        {
            line = line?.Trim() ?? "";
            if (line.Length == 0) return new PlayerInput.Empty();
            if (line[0] != '/') return new PlayerInput.Send(new Say(line));

            var (command, rest) = CommandLine.Split(line);
            return command switch
            {
                "tira" or "segreto" when rest.Length == 0 => new PlayerInput.Invalid($"Uso: /{command} <dadi>, es. /{command} d20+3"),
                "tira" => new PlayerInput.Send(new RollDice(rest)),
                "segreto" => new PlayerInput.Send(new RollDice(rest, Secret: true)),
                "sussurra" => CommandLine.FirstWord(rest) is ({ Length: > 0 } to, { Length: > 0 } text)
                    ? new PlayerInput.Send(new Whisper(to, text))
                    : new PlayerInput.Invalid("Uso: /sussurra <nome> <testo>"),
                "scheda" => new PlayerInput.Send(new ShowSheet(rest.Length == 0 ? null : rest)),
                "imposta" => CommandLine.FirstWord(rest) is ({ Length: > 0 } key, var value)
                    ? new PlayerInput.Send(new SetField(key, value))
                    : new PlayerInput.Invalid("Uso: /imposta <campo> [valore], es. /imposta Ferite 2"),
                "chi" => new PlayerInput.Send(new ListPlayers()),
                "aiuto" or "?" => new PlayerInput.ShowHelp(),
                "esci" => new PlayerInput.Quit(),
                _ => new PlayerInput.Invalid($"Comando sconosciuto: /{command}. Scrivi /aiuto."),
            };
        }
    }
}

// ===== src/LexArcana.Core/Net/WebGateway.cs =====
namespace LexArcana.Net
{
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
}

// ===== src/LexArcana.Core/Net/WebSocketConnection.cs =====
namespace LexArcana.Net
{
    /// <summary>
    /// Una connessione WebSocket dal browser (per esempio Safari su iPhone): ogni messaggio di testo è un messaggio JSON.
    /// Stessi limiti della connessione TCP: messaggi al massimo di <see cref="LineConnection.MaxLineBytes"/>
    /// e una coda d'uscita che, se si riempie, chiude la connessione invece di rallentare gli altri.
    /// </summary>
    public sealed class WebSocketConnection : IConnection
    {
        private readonly WebSocket _socket;
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(LineConnection.MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _closed = new();
        private readonly Task _writer;
        private readonly byte[] _buffer = new byte[4096];

        public WebSocketConnection(WebSocket socket, string remoteEndPoint)
        {
            _socket = socket;
            RemoteEndPoint = remoteEndPoint;
            _writer = WriteLoopAsync();
        }

        public string RemoteEndPoint { get; }
        public CancellationToken Closed => _closed.Token;

        public bool Post(string line)
        {
            if (_outbox.Writer.TryWrite(line)) return true;
            Close();
            return false;
        }

        public async Task FlushAsync(TimeSpan timeout)
        {
            _outbox.Writer.TryComplete();
            try { await _writer.WaitAsync(timeout).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(_buffer, linked.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // RFC 6455: a una richiesta di chiusura si risponde con la propria, poi si chiude.
                    _outbox.Writer.TryComplete();
                    await _writer.ConfigureAwait(false);
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text) throw new ProtocolException("Sono ammessi solo messaggi di testo.");
                message.Write(_buffer, 0, result.Count);
                if (message.Length > LineConnection.MaxLineBytes) throw new ProtocolException("Messaggio troppo lungo.");
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length).Trim();
                if (text.Length > 0) return text;
                message.SetLength(0);
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var line in _outbox.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
                    await _socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, _closed.Token).ConfigureAwait(false);
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
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
            _socket.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            await _writer.ConfigureAwait(false);
            _socket.Dispose();
            _closed.Dispose();
        }
    }
}

// ===== src/LexArcana.Core/Protocol/Messages.cs =====
namespace LexArcana.Protocol
{
    // Sul filo ogni messaggio è un oggetto JSON su una riga (UTF-8, terminata da '\n'),
    // con il campo "type" che dice quale record è, per esempio:
    //   {"type":"roll","expression":"3d6+2","secret":false}
    // I client inviano ClientMessage, il server risponde con ServerMessage.

    /// <summary>Un messaggio da un giocatore al server.</summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(Hello), "hello")]
    [JsonDerivedType(typeof(Say), "say")]
    [JsonDerivedType(typeof(RollDice), "roll")]
    [JsonDerivedType(typeof(Whisper), "whisper")]
    [JsonDerivedType(typeof(SetField), "set_field")]
    [JsonDerivedType(typeof(ShowSheet), "show_sheet")]
    [JsonDerivedType(typeof(ListPlayers), "list_players")]
    [JsonDerivedType(typeof(Leave), "leave")]
    [JsonDerivedType(typeof(Move), "move")]
    [JsonDerivedType(typeof(SetSheet), "set_sheet")]
    [JsonDerivedType(typeof(Embody), "embody")]
    [JsonDerivedType(typeof(Attack), "attack")]
    [JsonDerivedType(typeof(Command), "command")]
    public abstract record ClientMessage;

    /// <summary>Il primo messaggio di ogni client: chi è e, se serve, la parola d'ordine.</summary>
    public sealed record Hello(string Name, string? Password = null, int Version = Wire.ProtocolVersion) : ClientMessage;
    public sealed record Say(string Text) : ClientMessage;
    public sealed record RollDice(string Expression, bool Secret = false) : ClientMessage;
    public sealed record Whisper(string To, string Text) : ClientMessage;
    /// <summary>Scrive un campo della propria scheda; un valore vuoto lo cancella.</summary>
    public sealed record SetField(string Key, string Value) : ClientMessage;
    /// <summary>Chiede una scheda: la propria se <see cref="Owner"/> è null.</summary>
    public sealed record ShowSheet(string? Owner = null) : ClientMessage;
    public sealed record ListPlayers : ClientMessage;
    public sealed record Leave : ClientMessage;
    /// <summary>Dove si trova e dove guarda il personaggio (client 3D, circa 10 volte al secondo).</summary>
    public sealed record Move(float X, float Z, float Yaw) : ClientMessage;
    /// <summary>Scrive molti campi della propria scheda in una volta, es. quando si entra con un personaggio salvato.</summary>
    public sealed record SetSheet(Dictionary<string, string> Fields) : ClientMessage;
    /// <summary>Entra in gioco con un personaggio salvato: compila la scheda e dà le statistiche di combattimento.</summary>
    public sealed record Embody(LexArcana.Characters.Character Character) : ClientMessage;
    /// <summary>Colpisce un nemico: in mischia, o a distanza con <see cref="Ranged"/>.</summary>
    public sealed record Attack(int EnemyId, bool Ranged = false) : ClientMessage;
    /// <summary>Un comando del Demiurgo che conduce da remoto, es. "/narra …" o "/avventura 2".</summary>
    public sealed record Command(string Line) : ClientMessage;

    /// <summary>Un messaggio dal server a un giocatore (e al Demiurgo).</summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(Welcome), "welcome")]
    [JsonDerivedType(typeof(Joined), "joined")]
    [JsonDerivedType(typeof(Left), "left")]
    [JsonDerivedType(typeof(Said), "said")]
    [JsonDerivedType(typeof(Narrated), "narrated")]
    [JsonDerivedType(typeof(Rolled), "rolled")]
    [JsonDerivedType(typeof(Whispered), "whispered")]
    [JsonDerivedType(typeof(SheetShown), "sheet")]
    [JsonDerivedType(typeof(PlayerList), "players")]
    [JsonDerivedType(typeof(Refused), "refused")]
    [JsonDerivedType(typeof(Expelled), "expelled")]
    [JsonDerivedType(typeof(Moved), "moved")]
    [JsonDerivedType(typeof(MapChanged), "map_changed")]
    [JsonDerivedType(typeof(EnemyList), "enemies")]
    [JsonDerivedType(typeof(EnemyMoves), "enemy_moves")]
    [JsonDerivedType(typeof(Struck), "struck")]
    [JsonDerivedType(typeof(Vitals), "vitals")]
    public abstract record ServerMessage;

    /// <summary>L'accoglienza: chi c'è, il luogo (righe della mappa) e dove si trova ognuno, te compreso.</summary>
    public sealed record Welcome(string Name, string[] Players, MapData Map, Position[] Positions, int Version = Wire.ProtocolVersion) : ServerMessage;
    public sealed record MapData(string Name, string[] Rows);
    public sealed record Position(string Name, float X, float Z, float Yaw);
    public sealed record Joined(string Name) : ServerMessage;
    public sealed record Left(string Name) : ServerMessage;
    public sealed record Said(string From, string Text) : ServerMessage;
    public sealed record Narrated(string Text) : ServerMessage;
    /// <summary>Un tiro fatto dal server. I dadi sottratti (come in d6-d4) compaiono negativi.</summary>
    public sealed record Rolled(string From, string Expression, int[] Dice, int Modifier, int Total, bool Secret = false) : ServerMessage;
    public sealed record Whispered(string From, string To, string Text) : ServerMessage;
    /// <summary>Una scheda; <see cref="UpdatedBy"/> dice chi l'ha appena modificata, se è una modifica.</summary>
    public sealed record SheetShown(string Owner, Dictionary<string, string> Fields, string? UpdatedBy = null) : ServerMessage;
    public sealed record PlayerList(string[] Names) : ServerMessage;
    /// <summary>Una richiesta respinta; se arriva in risposta a <see cref="Hello"/> la connessione si chiude.</summary>
    public sealed record Refused(string Reason) : ServerMessage;
    public sealed record Expelled(string? Reason = null) : ServerMessage;
    /// <summary>Un personaggio si è spostato. Se riguarda te, è il server che corregge una mossa non valida.</summary>
    public sealed record Moved(string Name, float X, float Z, float Yaw) : ServerMessage;
    /// <summary>Il Demiurgo ha portato il gruppo in un altro luogo: nuova mappa, e tutti ripartono dai punti di partenza.</summary>
    public sealed record MapChanged(MapData Map, Position[] Positions, string? Title = null) : ServerMessage;

    /// <summary>Un nemico come lo vede un client: Kind è l'id del tipo nel bestiario (decide l'aspetto).</summary>
    public sealed record EnemyInfo(int Id, string Kind, string Name, int Level, int Health, int MaxHealth, float X, float Z, float Yaw, string Rank);
    public sealed record EnemyPose(int Id, float X, float Z, float Yaw);
    /// <summary>Tutti i nemici del luogo: sostituisce l'elenco precedente.</summary>
    public sealed record EnemyList(EnemyInfo[] Enemies) : ServerMessage;
    /// <summary>Gli spostamenti dei nemici dall'ultimo aggiornamento (circa 10 al secondo).</summary>
    public sealed record EnemyMoves(EnemyPose[] Poses) : ServerMessage;
    /// <summary>
    /// Un colpo. Attacker e Target sono nomi da mostrare; se l'uno o l'altro è un nemico, il suo id è in
    /// AttackerEnemyId o TargetEnemyId (null per i giocatori). Defeated dice se il bersaglio è caduto.
    /// </summary>
    public sealed record Struck(
        string Attacker, string Target,
        int Roll, int Total, int Against, bool Hit, bool Critical, int Damage,
        int Health, int MaxHealth, bool Defeated, bool Ranged = false,
        int? AttackerEnemyId = null, int? TargetEnemyId = null) : ServerMessage;
    /// <summary>Salute, livello ed esperienza di un giocatore.</summary>
    public sealed record Vitals(string Name, int Health, int MaxHealth, int Level, int Experience, int NextLevel, bool Down) : ServerMessage;
}

// ===== src/LexArcana.Core/Protocol/Wire.cs =====
namespace LexArcana.Protocol
{
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        UseStringEnumConverter = true,
        RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(ClientMessage))]
    [JsonSerializable(typeof(ServerMessage))]
    internal sealed partial class WireContext : JsonSerializerContext;

    /// <summary>Codifica e decodifica dei messaggi, generata a compilazione (niente reflection, compatibile con AOT).</summary>
    public static class Wire
    {
        public const int ProtocolVersion = 5;

        public static string Encode(ClientMessage message) => JsonSerializer.Serialize(message, WireContext.Default.ClientMessage);
        public static string Encode(ServerMessage message) => JsonSerializer.Serialize(message, WireContext.Default.ServerMessage);

        public static ClientMessage DecodeClient(string line) => Decode(line, WireContext.Default.ClientMessage);
        public static ServerMessage DecodeServer(string line) => Decode(line, WireContext.Default.ServerMessage);

        private static T Decode<T>(string line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize(line, type) ?? throw new ProtocolException("Messaggio vuoto.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new ProtocolException("Messaggio non valido: " + ex.Message);
            }
        }
    }

    public sealed class ProtocolException(string message) : Exception(message);
}

// ===== src/LexArcana.Core/Ui/MessageRenderer.cs =====
namespace LexArcana.Ui
{
    /// <summary>
    /// Trasforma un messaggio del server in testo da mostrare, dal punto di vista di <c>viewer</c>.
    /// Restituisce null per i messaggi che non si leggono, come gli spostamenti.
    /// </summary>
    public sealed class MessageRenderer(string viewer)
    {
        public StyledText? Render(ServerMessage message)
        {
            var t = new StyledText();
            switch (message)
            {
                case Welcome(var name, var players, var map, _, _):
                    t.Add("LEX ARCANA", Tone.Gilt, bold: true).Add($" · {map.Name.Sanitized} · Benvenuto, {name.Sanitized}. ", Tone.Muted);
                    Present(t, players).NewLine().Add("Scrivi /aiuto per i comandi.", Tone.Muted);
                    break;
                case Joined(var name):
                    t.Add($"» {name.Sanitized} entra nella sessione.", Tone.Verdigris);
                    break;
                case Left(var name):
                    t.Add($"« {name.Sanitized} lascia la sessione.", Tone.Muted);
                    break;
                case Said(var from, var text):
                    Speaker(t, from).Add(" " + text.Sanitized);
                    break;
                case Narrated(var text):
                    t.Add("❦ ", Tone.Gilt).Add(text.Sanitized, italic: true);
                    break;
                case Rolled roll:
                    Roll(t, roll);
                    break;
                case Whispered(var from, var to, var text):
                    t.Add(WhisperLead(from, to), Tone.Muted, italic: true).Add(text.Sanitized);
                    break;
                case SheetShown sheet:
                    Sheet(t, sheet);
                    break;
                case PlayerList(var names):
                    Present(t, names);
                    break;
                case Refused(var reason):
                    t.Add("! " + reason.Sanitized, Tone.Pompeian);
                    break;
                case Expelled(var reason):
                    t.Add(reason.Sanitized is { Length: > 0 } r ? r : "Il Demiurgo ti ha allontanato dalla sessione.", Tone.Pompeian);
                    break;
                case MapChanged(var map, _, var title):
                    t.Add("» Il gruppo raggiunge un nuovo luogo: ", Tone.Verdigris).Add(map.Name.Sanitized, Tone.Gilt, bold: true);
                    if (title is { Length: > 0 } && !title.Sanitized.EndsWith(map.Name.Sanitized, StringComparison.Ordinal))
                        t.Add($" · {title.Sanitized}", Tone.Muted);
                    break;
                case Struck s:
                    Blow(t, s);
                    break;
                case EnemyList(var enemies) when enemies.Length > 0:
                    t.Add($"† {enemies.Length} nemici nel luogo", Tone.Pompeian).Add(
                        $" · livello {enemies.Min(e => e.Level)}–{enemies.Max(e => e.Level)}" +
                        (enemies.FirstOrDefault(e => e.Rank == "Boss") is { } boss ? $" · li guida {boss.Name}" : ""), Tone.Muted);
                    break;
                default:
                    return null; // Moved, EnemyMoves, Vitals: si vedono nella scena 3D, non nel testo: si vedono nella scena 3D, non nel testo
            }
            return t;
        }

        private bool IsViewer(string name) => name.IsSameName(viewer);

        private void Blow(StyledText t, Struck s)
        {
            string who = IsViewer(s.Attacker) ? "Tu" : s.Attacker.Sanitized;
            string whom = IsViewer(s.Target) ? "te" : s.Target.Sanitized;
            var attackerTone = s.AttackerEnemyId is null ? Tone.Bronze : Tone.Pompeian;
            t.Add("† ", Tone.Pompeian).Add(who, attackerTone, bold: true);
            if (!s.Hit)
            {
                t.Add($" {(IsViewer(s.Attacker) ? "manchi" : "manca")} {whom}", Tone.Muted).Add($"  [{s.Roll}] {s.Total} contro {s.Against}", Tone.Muted);
                return;
            }
            string verb = s.Ranged ? (IsViewer(s.Attacker) ? "trafiggi" : "trafigge") : (IsViewer(s.Attacker) ? "colpisci" : "colpisce");
            t.Add($" {verb} {whom}", Tone.Ink);
            if (s.Critical) t.Add(" con un colpo critico", Tone.Gilt, bold: true);
            t.Add($"  [{s.Roll}] {s.Total} contro {s.Against}: ", Tone.Muted).Add($"{s.Damage} danni", Tone.Pompeian, bold: true)
             .Add($" ({s.Health}/{s.MaxHealth})", Tone.Muted);
            if (s.Defeated)
                t.Add(s.TargetEnemyId is null ? (IsViewer(s.Target) ? " · sei a terra!" : $" · {whom} è a terra!") : $" · {whom} è sconfitto.", Tone.Gilt, bold: true);
        }

        private static StyledText Speaker(StyledText t, string from) =>
            Names.IsDemiurge(from) ? t.Add(from + ":", Tone.Gilt, bold: true) : t.Add(from.Sanitized + ":", Tone.Bronze, bold: true);

        private string WhisperLead(string from, string to) =>
            IsViewer(from) ? $"Sussurri a {to.Sanitized}: " :
            IsViewer(to) ? $"{from.Sanitized} ti sussurra: " :
            $"{from.Sanitized} sussurra a {to.Sanitized}: ";

        private void Roll(StyledText t, Rolled r)
        {
            t.Add("◆ ", Tone.Bronze);
            if (IsViewer(r.From)) t.Add("Tiri", bold: true);
            else t.Add(r.From.Sanitized, Names.IsDemiurge(r.From) ? Tone.Gilt : Tone.Bronze, bold: true).Add(" tira", Tone.Muted);
            t.Add(" " + r.Expression.Sanitized, Tone.Muted);
            if (r.Secret) t.Add(" (segreto)", Tone.Muted, italic: true);
            var dice = string.Join(" ", r.Dice.Select(d => d < 0 ? $"−{-d}" : d.ToString(CultureInfo.InvariantCulture)));
            var modifier = r.Modifier switch { > 0 => $" +{r.Modifier}", < 0 => $" −{-r.Modifier}", _ => "" };
            t.Add($"  [{dice}]{modifier} = ", Tone.Muted).Add(r.Total.ToString(CultureInfo.InvariantCulture), Tone.Pompeian, bold: true);
        }

        private void Sheet(StyledText t, SheetShown s)
        {
            t.Add($"Scheda di {s.Owner.Sanitized}", Tone.Gilt, bold: true);
            if (s.UpdatedBy is { } by && !by.IsSameName(s.Owner) && !IsViewer(by))
                t.Add($" · aggiornata da {by.Sanitized}", Tone.Muted);
            if (s.Fields.Count == 0)
            {
                t.NewLine().Add("  ancora vuota: usa /imposta <campo> <valore>", Tone.Muted, italic: true);
                return;
            }
            int width = s.Fields.Keys.Max(k => k.Sanitized.Length);
            foreach (var (key, value) in s.Fields)
                t.NewLine().Add("  " + key.Sanitized.PadRight(width) + "  ", Tone.Muted).Add(value.Sanitized);
        }

        private static StyledText Present(StyledText t, string[] names)
        {
            t.Add("Presenti: ", Tone.Muted);
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) t.Add(", ", Tone.Muted);
                t.Add(names[i].Sanitized, Names.IsDemiurge(names[i]) ? Tone.Gilt : Tone.Bronze);
            }
            return t;
        }
    }
}

// ===== src/LexArcana.Core/Ui/Palette.cs =====
namespace LexArcana.Ui
{
    /// <summary>
    /// I colori del design system Lex Arcana (tema Night, pensato per sfondi scuri).
    /// Per il terminale li trasforma in sequenze ANSI a 24 bit; con <c>enabled: false</c> il testo esce senza colori.
    /// </summary>
    public sealed class Palette(bool enabled)
    {
        public bool Enabled => enabled;

        /// <summary>Il colore di un tono nel tema Night, come (r, g, b).</summary>
        public static (byte R, byte G, byte B) Rgb(Tone tone) => tone switch
        {
            Tone.Ink => (0xee, 0xe4, 0xd2),
            Tone.Muted => (0xb8, 0xa8, 0x92),
            Tone.Pompeian => (0xe8, 0x80, 0x6a),
            Tone.Bronze => (0xd3, 0xa9, 0x5e),
            Tone.Verdigris => (0x72, 0xbb, 0xa8),
            Tone.Gilt => (0xc4, 0xa0, 0x58),
            _ => (0xee, 0xe4, 0xd2),
        };

        public string Format(StyledText text)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < text.Lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                foreach (var span in text.Lines[i]) Append(sb, span);
            }
            return sb.ToString();
        }

        public string Format(string text, Tone tone, bool bold = false, bool italic = false) =>
            Format(new StyledText().Add(text, tone, bold, italic));

        public string Ink(string s) => Format(s, Tone.Ink);
        public string Muted(string s) => Format(s, Tone.Muted);
        public string Pompeian(string s) => Format(s, Tone.Pompeian);
        public string Bronze(string s) => Format(s, Tone.Bronze);
        public string Gilt(string s) => Format(s, Tone.Gilt);
        public string Bold(string s) => enabled ? $"\e[1m{s}\e[22m" : s;

        private void Append(StringBuilder sb, Span span)
        {
            if (!enabled)
            {
                sb.Append(span.Text);
                return;
            }
            var (r, g, b) = Rgb(span.Tone);
            sb.Append($"\e[38;2;{r};{g};{b}m");
            if (span.Bold) sb.Append("\e[1m");
            if (span.Italic) sb.Append("\e[3m");
            sb.Append(span.Text).Append("\e[0m");
        }

        /// <summary>Colori attivi se l'uscita è un terminale e la variabile NO_COLOR non è impostata.</summary>
        public static bool ShouldUseColor() =>
            !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    }
}

// ===== src/LexArcana.Core/Ui/StyledText.cs =====
namespace LexArcana.Ui
{
    /// <summary>I colori del design system Lex Arcana con cui si scrive il testo.</summary>
    public enum Tone { Ink, Muted, Pompeian, Bronze, Verdigris, Gilt }

    /// <summary>Un pezzo di testo con il suo stile.</summary>
    public readonly record struct Span(string Text, Tone Tone = Tone.Ink, bool Bold = false, bool Italic = false);

    /// <summary>Un messaggio da mostrare: una o più righe fatte di <see cref="Span"/>.</summary>
    public sealed class StyledText
    {
        private readonly List<List<Span>> _lines = [[]];

        public IReadOnlyList<IReadOnlyList<Span>> Lines => _lines;

        public StyledText Add(string text, Tone tone = Tone.Ink, bool bold = false, bool italic = false)
        {
            if (text.Length > 0) _lines[^1].Add(new(text, tone, bold, italic));
            return this;
        }

        public StyledText NewLine()
        {
            _lines.Add([]);
            return this;
        }

        public string PlainText => string.Join('\n', _lines.Select(l => string.Concat(l.Select(s => s.Text))));

        public override string ToString() => PlainText;
    }
}

// ===== src/LexArcana.Core/Versions.cs =====
namespace LexArcana
{
    /// <summary>Le versioni del gioco e del runtime .NET con cui gira (incluso nell'eseguibile, se è autosufficiente).</summary>
    public static class Versions
    {
        public static string Game =>
            typeof(Versions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

        public static string Runtime => RuntimeInformation.FrameworkDescription;

        public static string Describe(string program) =>
            $"{program} {Game} · {Runtime} · {RuntimeInformation.RuntimeIdentifier}";
    }
}

// ===== src/LexArcana/CampaignConsole.cs =====
namespace LexArcana.App
{
    /// <summary>Genera una campagna e la scrive su disco.</summary>
    internal static class CampaignConsole
    {
        public static int Run(Options options, Palette p)
        {
            int seed = options.Seed ?? Random.Shared.Next(1, 1_000_000);
            var campaign = CampaignGenerator.Generate(seed, options.Adventures);
            var folder = options.Folder ?? Path.Combine("campagne", Slug(campaign.Title));
            IReadOnlyList<string> files;
            try
            {
                files = CampaignFiles.Write(campaign, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(p.Pompeian($"Impossibile scrivere in {folder}: {ex.Message}"));
                return 1;
            }

            Terminal.WriteLine(p.Gilt(p.Bold(campaign.Title)) + p.Muted($" · seme {seed}"));
            Terminal.WriteLine(p.Ink(campaign.Premise));
            foreach (var a in campaign.Adventures)
                Terminal.WriteLine($"  {p.Bronze($"{a.Number}.")} {p.Ink(a.Title)} {p.Muted($"· {a.Place}")}");
            Terminal.WriteLine(p.Muted($"\nScritti {files.Count} file in {folder}. Il manuale del Demiurgo è campagna.md."));
            Terminal.WriteLine(p.Muted("Per giocare: ") + p.Ink($"lexarcana server --campagna {Path.Combine(folder, "campagna.json")}"));
            return 0;
        }

        private static string Slug(string title)
        {
            var chars = title.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .Select(c => char.IsLetterOrDigit(c) ? c : '-');
            return string.Join('-', new string([.. chars]).Split('-', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

// ===== src/LexArcana/CharacterConsole.cs =====
namespace LexArcana.App
{
    /// <summary>Il creatore di personaggi da terminale: guidato passo per passo, oppure casuale.</summary>
    internal static class CharacterConsole
    {
        public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
        {
            Character character;
            if (options.RandomCharacter)
            {
                character = Character.Random(options.Name!, options.Seed ?? Random.Shared.Next());
            }
            else
            {
                var built = await GuideAsync(options.Name!, p, stop);
                if (built is null) return 1;
                character = built;
            }

            Show(character, p);
            var path = options.CharacterFile ?? CharacterFile.DefaultPath(character);
            try
            {
                await File.WriteAllTextAsync(path, character.ToJson(), stop);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile salvare {path}: {ex.Message}"));
                return 1;
            }
            Terminal.WriteLine(p.Muted("Salvato in ") + p.Ink(path) + p.Muted(". Portalo in sessione con: ") +
                               p.Ink($"lexarcana-visione --personaggio {path} --host <indirizzo>"));
            return 0;
        }

        private static async Task<Character?> GuideAsync(string player, Palette p, CancellationToken stop)
        {
            var lines = Terminal.ReadLinesAsync(stop).GetAsyncEnumerator(stop);
            async Task<string?> Ask(string question)
            {
                Terminal.WriteLine(p.Bronze("› ") + p.Ink(question));
                return await lines.MoveNextAsync() ? lines.Current.Trim() : null;
            }
            async Task<T?> Choose<T>(string title, IReadOnlyList<T> items, Func<T, string> name, Func<T, string> describe) where T : class
            {
                Terminal.WriteLine("\n" + p.Gilt(p.Bold(title)));
                for (int i = 0; i < items.Count; i++) Terminal.WriteLine($"  {p.Bronze((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2))}  {describe(items[i])}");
                while (true)
                {
                    var answer = await Ask($"Scegli un numero da 1 a {items.Count} (o scrivi il nome):");
                    if (answer is null) return null;
                    if (int.TryParse(answer, out int n) && n >= 1 && n <= items.Count) return items[n - 1];
                    if (items.FirstOrDefault(i => name(i).Equals(answer, StringComparison.OrdinalIgnoreCase)) is { } named) return named;
                    Terminal.WriteLine(p.Pompeian("! Scrivi il numero o il nome di una delle voci."));
                }
            }

            var builder = new CharacterBuilder(player);
            Terminal.WriteLine(p.Gilt(p.Bold("CREAZIONE DEL PERSONAGGIO")) + p.Muted($" · giocatore {player}"));
            Terminal.WriteLine(p.Muted("Le prove si fanno con 1d20 + attributo + abilità contro 10 (facile), 15 (normale) o 20 (difficile)."));

            var vocation = await Choose("Vocazione", Rules.Vocations, v => v.Name,
                v => $"{p.Ink(v.Name.PadRight(12))}{p.Muted($"+1 {v.Bonus} · {string.Join(", ", v.Skills)} · {v.Description}")}");
            if (vocation is null) return null;
            builder.Choose(vocation);

            var origin = await Choose("Origine", Rules.Origins, o => o.Name,
                o => $"{p.Ink(o.Name.PadRight(12))}{p.Muted($"+1 {o.Bonus} · parla {o.Language} · {o.Description}")}");
            if (origin is null) return null;
            builder.Choose(origin);

            Terminal.WriteLine("\n" + p.Gilt(p.Bold("Attributi")) + p.Muted($" · ognuno parte da 1; hai {Rules.AttributePoints} punti, al massimo {Rules.AttributeMaximumBeforeBonuses} prima dei bonus. L'ultimo prende i punti rimasti."));
            while (true)
            {
                foreach (var t in Rules.Traits) builder.TrySetAttribute(t, Rules.AttributeMinimum, out _);
                bool ok = true;
                for (int i = 0; i < Rules.Traits.Count && ok; i++)
                {
                    var trait = Rules.Traits[i];
                    if (i == Rules.Traits.Count - 1)
                    {
                        int value = Rules.AttributeMinimum + builder.PointsLeft;
                        ok = builder.TrySetAttribute(trait, value, out _);
                        Terminal.WriteLine(ok
                            ? p.Muted($"  {trait} ({Rules.Describe(trait)}) prende i punti rimasti: ") + p.Ink(value.ToString(CultureInfo.InvariantCulture))
                            : p.Pompeian($"! Per {trait} resterebbero {value} punti, oltre il massimo di {Rules.AttributeMaximumBeforeBonuses}. Ricominciamo."));
                        break;
                    }
                    while (true)
                    {
                        var answer = await Ask($"{trait} ({Rules.Describe(trait)}): da 1 a {Rules.AttributeMaximumBeforeBonuses}, punti rimasti {builder.PointsLeft}");
                        if (answer is null) return null;
                        if (!int.TryParse(answer, out int value)) { Terminal.WriteLine(p.Pompeian("! Scrivi un numero.")); continue; }
                        if (builder.TrySetAttribute(trait, value, out var error)) break;
                        Terminal.WriteLine(p.Pompeian("! " + error));
                    }
                }
                if (ok) break;
            }
            return await FinishAsync();

            async Task<Character?> FinishAsync()
            {
                var free = Rules.Skills.Where(s => !vocation.Skills.Contains(s.Name)).ToList();
                Terminal.WriteLine("\n" + p.Gilt(p.Bold("Abilità")) + p.Muted($" · la vocazione ti dà +{Rules.VocationSkillRank} in {string.Join(", ", vocation.Skills)}; scegline altre {Rules.ChosenSkills} a +{Rules.ChosenSkillRank}."));
                while (builder.ChosenSkills.Count < Rules.ChosenSkills)
                {
                    var skill = await Choose($"Abilità {builder.ChosenSkills.Count + 1} di {Rules.ChosenSkills}", free.Where(f => !builder.ChosenSkills.Contains(f.Name)).ToList(), s => s.Name,
                        s => $"{p.Ink(s.Name.PadRight(14))}{p.Muted($"{s.Trait} · {s.Description}")}");
                    if (skill is null) return null;
                    if (!builder.TryChooseSkill(skill.Name, out var error)) Terminal.WriteLine(p.Pompeian("! " + error));
                }

                var suggestion = RomanNames.Generate(Random.Shared, Random.Shared.Next(2) == 0);
                var name = await Ask($"Nome romano del personaggio (Invio per «{suggestion}»):");
                if (name is null) return null;
                builder.SetRomanName(name.Length == 0 ? suggestion : name);
                builder.Concept = await Ask("Una frase sul personaggio (facoltativa):") ?? "";

                if (builder.TryBuild(out var character, out var errors)) return character;
                foreach (var e in errors) Terminal.WriteLine(p.Pompeian("! " + e));
                return null;
            }
        }

        private static void Show(Character c, Palette p)
        {
            Terminal.WriteLine("\n" + p.Gilt(p.Bold(c.RomanName)) + p.Muted($" · {c.Vocation} da {c.Origin} · giocatore {c.Player}"));
            foreach (var (key, value) in c.ToSheet().Skip(1))
                Terminal.WriteLine("  " + p.Muted(key.PadRight(16)) + p.Ink(value));
        }
    }
}

// ===== src/LexArcana/DemiurgeConsole.cs =====
namespace LexArcana.App
{
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
}

// ===== src/LexArcana/Options.cs =====
namespace LexArcana.App
{
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
}

// ===== src/LexArcana/PlayerConsole.cs =====
namespace LexArcana.App
{
    /// <summary>Il terminale di un giocatore collegato al server del Demiurgo.</summary>
    internal static class PlayerConsole
    {
        public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
        {
            var host = options.Host ?? "127.0.0.1";
            Character? character = null;
            if (options.CharacterFile is { } file)
            {
                if (!CharacterFile.TryLoad(file, out var loaded, out var characterError))
                {
                    await Console.Error.WriteLineAsync(p.Pompeian(characterError));
                    return 1;
                }
                character = loaded;
            }
            var name = options.Name ?? character!.Player;
            GameClient client;
            try
            {
                client = await GameClient.ConnectToAsync(host, options.Port, name, options.Password, stop);
            }
            catch (ConnectionRefusedException ex)
            {
                await Console.Error.WriteLineAsync(p.Pompeian("Il server ha rifiutato l'accesso: " + ex.Message));
                return 1;
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException)
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile collegarsi al tavolo {host}: {ex.Message}"));
                return 1;
            }
            catch (SocketException ex)
            {
                await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile collegarsi a {host}:{options.Port}: {ex.Message}"));
                return 1;
            }

            await using (client)
            {
                var renderer = new MessageRenderer(client.Name);
                Terminal.WriteLine(p, renderer.Render(client.Welcome));
                if (character is not null)
                    client.Send(new Embody(character));

                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var receiving = ReceiveAsync(client, renderer, p, session);
                bool demiurge = LexArcana.Game.Names.IsDemiurge(client.Name);
                if (demiurge) Terminal.WriteLine(p.Gilt("Conduci da remoto: ") + p.Muted("ogni riga è un comando del Demiurgo, /aiuto per l'elenco, /esci per lasciare."));
                await foreach (var line in Terminal.ReadLinesAsync(session.Token))
                {
                    if (demiurge)
                    {
                        if (line.Trim() == "/esci") { await session.CancelAsync(); break; }
                        if (line.Trim().Length > 0 && !client.Send(new Command(line))) await session.CancelAsync();
                        continue;
                    }
                    switch (PlayerCommands.Parse(line))
                    {
                        case PlayerInput.Send(var message):
                            if (!client.Send(message)) await session.CancelAsync();
                            break;
                        case PlayerInput.ShowHelp:
                            Terminal.WriteLine(p.Muted(PlayerCommands.Help));
                            break;
                        case PlayerInput.Invalid(var reason):
                            Terminal.WriteLine(p.Pompeian("! " + reason));
                            break;
                        case PlayerInput.Quit:
                            await session.CancelAsync();
                            break;
                    }
                }
                await session.CancelAsync();
                return await receiving;
            }
        }

        /// <summary>Mostra i messaggi del server; restituisce 0 se si è usciti di propria volontà.</summary>
        private static async Task<int> ReceiveAsync(GameClient client, MessageRenderer renderer, Palette p, CancellationTokenSource session)
        {
            string? farewell = "Connessione al server persa.";
            try
            {
                await foreach (var message in client.ReadAllAsync(session.Token))
                {
                    Terminal.WriteLine(p, renderer.Render(message));
                    if (message is Expelled) farewell = null; // il motivo è già stato mostrato
                }
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (ProtocolException ex)
            {
                farewell = ex.Message;
            }

            if (farewell is not null) Terminal.WriteLine(p.Muted(farewell));
            await session.CancelAsync();
            return 1;
        }
    }
}

// ===== src/LexArcana/Terminal.cs =====
namespace LexArcana.App
{
    /// <summary>Scrive sul terminale da più thread senza mescolare le righe.</summary>
    internal static class Terminal
    {
        private static readonly Lock Gate = new();

        public static void WriteLine(string text)
        {
            lock (Gate) Console.WriteLine(text);
        }

        /// <summary>Scrive un messaggio già impaginato; non fa nulla se è null (es. uno spostamento).</summary>
        public static void WriteLine(Ui.Palette palette, Ui.StyledText? text)
        {
            if (text is not null) WriteLine(palette.Format(text));
        }

        /// <summary>Le righe digitate, finché l'ingresso resta aperto o finché <paramref name="ct"/> non si attiva.</summary>
        public static async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await input.ReadLineAsync(ct); }
                catch (OperationCanceledException) { yield break; }
                if (line is null) yield break;
                yield return line;
            }
        }
    }
}

