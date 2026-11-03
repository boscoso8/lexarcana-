// Red3D.cs — Lex Arcana: rete e 3D in un solo file.
// Generato da tools/GeneraRed3D.cs: non modificarlo a mano, modifica i sorgenti in src/ e rigeneralo.
// Contiene 37 file sorgente. Compila con Red3D.csproj (net10.0, C# 14, Raylib-cs).
//
// Avvio del client 3D: dotnet run --project Red3D -- --personaggio aurelia.json --host <indirizzo del Demiurgo>
// Il server e gli strumenti (personaggi, campagne) sono nel progetto completo: src/LexArcana.

using LexArcana.Campaigns;
using LexArcana.Characters;
using LexArcana.Combat;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Protocol;
using LexArcana.Ui;
using LexArcana.Visione;
using Raylib_cs;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Channels;

// ===== src/LexArcana.Visione/Program.cs =====
// Tutto avviene sul thread principale: la finestra (GLFW) lo richiede, soprattutto su macOS.
if (!VisioneOptions.TryParse(args, out var options, out var error))
{
    if (error.Length > 0) Console.Error.WriteLine(error + "\n");
    Console.Error.WriteLine(VisioneOptions.Usage);
    return error.Length > 0 ? 2 : 0;
}

GameClient client;
try
{
    client = GameClient.ConnectAsync(options.Host, options.Port, options.Name, options.Password).GetAwaiter().GetResult();
}
catch (ConnectionRefusedException ex)
{
    Console.Error.WriteLine("Il server ha rifiutato l'accesso: " + ex.Message);
    return 1;
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Impossibile collegarsi a {options.Host}:{options.Port}: {ex.Message}");
    return 1;
}

try
{
    if (!GameMap.TryParse([$"nome: {client.Welcome.Map.Name}", .. client.Welcome.Map.Rows], out var map, out var mapError))
    {
        Console.Error.WriteLine("Il server ha mandato una mappa non valida: " + mapError);
        return 1;
    }
    using var app = new VisioneApp(client, map, options);
    return app.Run();
}
finally
{
    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        public static Character Random(string player, int seed)
        {
            var random = new Random(seed);
            var vocation = Rules.Vocations[random.Next(Rules.Vocations.Count)];
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
        private readonly LineConnection _connection;

        private GameClient(LineConnection connection, Welcome welcome)
        {
            _connection = connection;
            Welcome = welcome;
        }

        public Welcome Welcome { get; }
        public string Name => Welcome.Name;

        /// <summary>Si collega e si presenta. Se il server rifiuta, lancia <see cref="ConnectionRefusedException"/> con il motivo.</summary>
        public static async Task<GameClient> ConnectAsync(string host, int port, string name, string? password = null, CancellationToken ct = default)
        {
            var tcp = new TcpClient();
            LineConnection? connection = null;
            try
            {
                await tcp.ConnectAsync(host, port, ct);
                connection = new LineConnection(tcp);
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
                if (connection is not null) await connection.DisposeAsync();
                tcp.Dispose();
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
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { yield break; }
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
    public sealed class GameServer(IPAddress address, int port, Action<ServerMessage> demiurgeView, string? password = null, Dice? dice = null, GameMap? map = null, Campaign? campaign = null, bool peaceful = false)
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

        private sealed record Player(string Name, LineConnection Connection)
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

                var session = RunSessionAsync(new LineConnection(tcp), ct);
                _sessions.TryAdd(session, 0);
                _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
            }
        }

        private async Task RunSessionAsync(LineConnection connection, CancellationToken ct)
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

        private async Task<Player?> HandshakeAsync(LineConnection connection, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HelloTimeout);
            if (await connection.ReadLineAsync(timeout.Token) is not { } line) return null;

            var player = Wire.DecodeClient(line) switch
            {
                not Hello => Refuse("Il primo messaggio deve essere hello."),
                Hello { Version: not Wire.ProtocolVersion } => Refuse($"Versione del protocollo non supportata: il server usa la {Wire.ProtocolVersion}. Aggiorna il client."),
                Hello h when !PasswordMatches(h.Password) => Refuse("Parola d'ordine errata."),
                Hello h when !Names.IsValid(h.Name) => Refuse($"Scegli un nome di 1–{Names.MaxLength} lettere o cifre, senza spazi."),
                Hello h when Names.IsDemiurge(h.Name) => Refuse($"Il nome «{Names.Demiurge}» è riservato a chi conduce la partita."),
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
            demiurgeView(rolled); // un tiro segreto lo vedono solo chi tira e il Demiurgo
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
            demiurgeView(whisper); // il Demiurgo sente ogni sussurro
            target?.Send(whisper);
            if (!Names.IsDemiurge(from) && _players.TryGetValue(from, out var sender)) sender.Send(whisper);
            return null;
        }

        private ServerMessage SetSheetField(string actor, string owner, string? key, string? value)
        {
            var sheet = SheetOf(owner);
            if (!sheet.TrySet(key, value, out var error)) return new Refused(error);
            var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), actor);
            if (!Names.IsDemiurge(actor)) demiurgeView(updated);
            return updated;
        }

        private ServerMessage SetSheetFields(string owner, Dictionary<string, string> fields)
        {
            var sheet = SheetOf(owner);
            if (fields.Count > CharacterSheet.MaxFields) return new Refused($"Una scheda ha al massimo {CharacterSheet.MaxFields} campi.");
            foreach (var (key, value) in fields)
                if (!sheet.TrySet(key, value, out var error)) return new Refused($"{key.Sanitized}: {error}");
            var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), owner);
            demiurgeView(updated);
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
            demiurgeView(message);
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
            if (_ticker is not null) await _ticker;
            foreach (var player in _players.Values) player.Connection.Close();
            if (_acceptLoop is not null) await _acceptLoop;
            await Task.WhenAll(_sessions.Keys);
            _stopping.Dispose();
        }
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
    public sealed class LineConnection : IAsyncDisposable
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

// ===== src/LexArcana.Visione/Avatar.cs =====
namespace LexArcana.Visione
{
    /// <summary>Un altro personaggio nella scena. Scivola verso l'ultima posizione ricevuta, così il movimento resta fluido.</summary>
    internal sealed class Avatar(string name, Pose pose)
    {
        public string Name { get; } = name;
        public Pose Target { get; set; } = pose;
        public Vector2 Position { get; private set; } = new(pose.X, pose.Z);
        public float Yaw { get; private set; } = pose.Yaw;

        /// <summary>Quanto sta camminando (0–1), per l'oscillazione del passo.</summary>
        public float Stride { get; private set; }
        public float Phase { get; private set; }

        public void Update(float dt)
        {
            var target = new Vector2(Target.X, Target.Z);
            var k = 1f - MathF.Exp(-12f * dt);
            var before = Position;
            Position = Vector2.Distance(Position, target) > 6f ? target : Vector2.Lerp(Position, target, k);
            float delta = MathF.IEEERemainder(Target.Yaw - Yaw, MathF.Tau);
            Yaw += delta * k;

            float speed = Vector2.Distance(before, Position) / MathF.Max(dt, 1e-4f);
            Stride += (Math.Clamp(speed / 3f, 0f, 1f) - Stride) * k;
            Phase += speed * dt * 3.2f;
        }
    }
}

// ===== src/LexArcana.Visione/Enemies.cs =====
namespace LexArcana.Visione
{
    /// <summary>Un nemico come lo vede il client: scivola verso la posizione ricevuta, lampeggia se colpito, svanisce se sconfitto.</summary>
    internal sealed class EnemyView(EnemyInfo info)
    {
        public int Id { get; } = info.Id;
        public string Kind { get; } = info.Kind;
        public string Name { get; } = info.Name;
        public int Level { get; } = info.Level;
        public string Rank { get; } = info.Rank;
        public int Health { get; set; } = info.Health;
        public int MaxHealth { get; } = info.MaxHealth;
        public Vector2 Target { get; set; } = new(info.X, info.Z);
        public float TargetYaw { get; set; } = info.Yaw;
        public Vector2 Position { get; private set; } = new(info.X, info.Z);
        public float Yaw { get; private set; } = info.Yaw;
        public double FlashUntil { get; set; }
        public double AttackAt { get; set; } = -10;
        public double? DiedAt { get; set; }
        public float Phase { get; private set; } = info.Id * 1.7f;
        public float Stride { get; private set; }

        public float Scale => Rank switch { "Boss" => 1.35f, "Elite" => 1.15f, _ => 1f };
        public string Label => $"{Name} · liv. {Level}";
        public float Height => (Kind switch { "lupo" => 1.0f, "strige" => 2.4f, "custode" => 2.5f, _ => 1.9f }) * Scale;

        public void Update(float dt)
        {
            float k = 1f - MathF.Exp(-12f * dt);
            var before = Position;
            Position = Vector2.Distance(Position, Target) > 6f ? Target : Vector2.Lerp(Position, Target, k);
            Yaw += MathF.IEEERemainder(TargetYaw - Yaw, MathF.Tau) * k;
            float speed = Vector2.Distance(before, Position) / MathF.Max(dt, 1e-4f);
            Stride += (Math.Clamp(speed / 3f, 0f, 1f) - Stride) * k;
            Phase += dt * (2f + speed * 3f);
        }
    }

    /// <summary>Un numero che sale e svanisce sopra chi è stato colpito.</summary>
    internal readonly record struct FloatingText(Vector3 Position, string Text, LexArcana.Ui.Tone Tone, double Time);

    /// <summary>Un dardo in volo, dal giocatore al bersaglio.</summary>
    internal readonly record struct Shot(Vector3 From, Vector3 To, double Time);
}

// ===== src/LexArcana.Visione/Hud.cs =====
namespace LexArcana.Visione
{
    /// <summary>
    /// Tutto ciò che sta sopra la scena: la chat, il campo di scrittura, i nomi sopra le teste,
    /// il mirino e i pannelli di aiuto. Il testo usa i font del design system (EB Garamond e Cinzel).
    /// </summary>
    internal sealed partial class Hud : IDisposable
    {
        private const int MaxLog = 60;
        private const int VisibleMessages = 8;
        private const double FadeAfter = 14;
        private const float TextSize = 22, LineHeight = 27, Margin = 16;

        private readonly Font _text, _caps;
        private readonly List<(StyledText Text, double Time)> _log = [];
        private readonly StringBuilder _input = new();
        private (string Text, double Time)? _banner;

        public Hud(string assets)
        {
            int[] codepoints = [.. Enumerable.Range(32, 95), .. Enumerable.Range(160, 96),
                0x2013, 0x2014, 0x2018, 0x2020, 0x2021, 0x2019, 0x201C, 0x201D, 0x2026, 0x2022, 0x2192, 0x2212, 0x2766, 0x25C6];
            _text = Raylib.LoadFontEx(Path.Combine(assets, "Fonts", "EBGaramond.ttf"), 44, codepoints, codepoints.Length);
            _caps = Raylib.LoadFontEx(Path.Combine(assets, "Fonts", "Cinzel.ttf"), 56, codepoints, codepoints.Length);
            Raylib.SetTextureFilter(_text.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureFilter(_caps.Texture, TextureFilter.Bilinear);
        }

        public bool Typing { get; private set; }
        public string Input => _input.ToString();

        public void Add(StyledText text, double now)
        {
            _log.Add((text, now));
            if (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        /// <summary>Una narrazione del Demiurgo: compare anche in grande al centro dello schermo.</summary>
        public void Banner(string text, double now) => _banner = (text, now);

        public void OpenInput(string prefix = "")
        {
            Typing = true;
            _input.Clear().Append(prefix);
            while (Raylib.GetCharPressed() != 0) { } // il tasto che ha aperto la chat non va scritto
        }

        /// <summary>Gestisce la tastiera mentre si scrive; restituisce la riga quando si preme Invio.</summary>
        public string? UpdateInput()
        {
            for (int c = Raylib.GetCharPressed(); c != 0; c = Raylib.GetCharPressed())
                if (c >= 32 && _input.Length < 500) _input.Append(char.ConvertFromUtf32(c));

            if ((Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace)) && _input.Length > 0)
            {
                int remove = _input.Length >= 2 && char.IsLowSurrogate(_input[^1]) ? 2 : 1;
                _input.Remove(_input.Length - remove, remove);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                Typing = false;
                return null;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            {
                Typing = false;
                return _input.ToString();
            }
            return null;
        }

        public void DrawPlaceName(string name)
        {
            DrawCaps(name.ToUpperInvariant(), new(Margin + 2, Margin + 2), 22, Theme.WithAlpha(Theme.Night, 200));
            DrawCaps(name.ToUpperInvariant(), new(Margin, Margin), 22, Theme.Of(Tone.Gilt));
        }

        public static void DrawCrosshair()
        {
            int cx = Raylib.GetScreenWidth() / 2, cy = Raylib.GetScreenHeight() / 2;
            Raylib.DrawCircle(cx, cy, 3.5f, Theme.WithAlpha(Theme.Night, 160));
            Raylib.DrawCircle(cx, cy, 2f, Theme.Of(Tone.Ink, 220));
        }

        public void DrawNameTag(string name, Vector2 screen, float distance)
        {
            float size = Math.Clamp(26f - distance * 0.8f, 14f, 24f);
            var measure = Raylib.MeasureTextEx(_caps, name, size, 1);
            var at = new Vector2(screen.X - measure.X / 2, screen.Y - measure.Y);
            var rect = new Rectangle(at.X - 8, at.Y - 2, measure.X + 16, measure.Y + 4);
            Raylib.DrawRectangleRounded(rect, 0.3f, 6, Theme.WithAlpha(Theme.Night, 150));
            DrawCaps(name, at, size, Theme.Of(Tone.Ink));
        }

        public void Draw(double now, bool showHelp, IReadOnlyList<string> present, string? overlay)
        {
            int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            float width = MathF.Min(720, w - 2 * Margin);

            // Narrazione in evidenza
            if (_banner is var (bannerText, bannerTime) && now - bannerTime < 9)
            {
                byte alpha = (byte)(255 * Math.Clamp((9 - (now - bannerTime)) / 1.5, 0, 1));
                var lines = Wrap(new StyledText().Add("❦ ", Tone.Gilt).Add(bannerText), MathF.Min(820, w - 80), 28);
                float y = h * 0.16f;
                float panelHeight = lines.Count * 36 + 24;
                Raylib.DrawRectangleRec(new(w / 2f - MathF.Min(860, w - 40) / 2, y - 12, MathF.Min(860, w - 40), panelHeight), Theme.WithAlpha(Theme.Panel, (byte)(alpha * 0.8)));
                foreach (var line in lines)
                {
                    float lineWidth = line.Sum(p => Measure(p.Text, 28));
                    DrawLine(line, new(w / 2f - lineWidth / 2, y), 28, alpha);
                    y += 36;
                }
            }

            // Registro della chat, dal basso verso l'alto
            var recent = _log.Where(e => Typing || now - e.Time < FadeAfter + 2).TakeLast(VisibleMessages).ToList();
            var laid = recent.Select(e => (Lines: Wrap(e.Text, width - 24, TextSize), e.Time)).ToList();
            float bottom = h - Margin - (Typing ? LineHeight + 20 : 0);
            float total = laid.Sum(l => l.Lines.Count) * LineHeight;
            if (laid.Count > 0)
            {
                byte panelAlpha = (byte)(Typing ? 200 : 140);
                Raylib.DrawRectangleRec(new(Margin, bottom - total - 12, width, total + 16), Theme.WithAlpha(Theme.Panel, panelAlpha));
            }
            float yLog = bottom - total - 4;
            foreach (var (lines, time) in laid)
            {
                byte alpha = Typing ? (byte)255 : (byte)(255 * Math.Clamp((FadeAfter + 2 - (now - time)) / 2, 0, 1));
                foreach (var line in lines)
                {
                    DrawLine(line, new(Margin + 12, yLog), TextSize, alpha);
                    yLog += LineHeight;
                }
            }

            // Campo di scrittura
            if (Typing)
            {
                var box = new Rectangle(Margin, h - Margin - LineHeight - 12, width, LineHeight + 12);
                Raylib.DrawRectangleRec(box, Theme.Panel);
                Raylib.DrawRectangleLinesEx(box, 1, Theme.Of(Tone.Gilt));
                bool caret = (int)(now * 2) % 2 == 0;
                DrawText(_input + (caret ? "|" : " "), new(box.X + 12, box.Y + 5), TextSize, Theme.Of(Tone.Ink));
            }
            else
            {
                DrawText("Invio per parlare · F1 aiuto", new(w - Margin - Measure("Invio per parlare · F1 aiuto", 18), h - Margin - 20), 18, Theme.Of(Tone.Muted, 190));
            }

            if (present.Count > 0 && Raylib.IsKeyDown(KeyboardKey.Tab)) DrawPresent(present, w);
            if (showHelp) DrawHelp(w, h);
            if (overlay is not null) DrawOverlay(overlay, w, h);
        }

        private void DrawPresent(IReadOnlyList<string> present, int w)
        {
            float panelWidth = 300, y = 70;
            Raylib.DrawRectangleRec(new(w - panelWidth - Margin, y - 10, panelWidth, 50 + present.Count * LineHeight), Theme.Panel);
            DrawCaps("PRESENTI", new(w - panelWidth - Margin + 16, y), 18, Theme.Of(Tone.Gilt));
            y += 34;
            foreach (var name in present)
            {
                var tone = LexArcana.Game.Names.IsDemiurge(name) ? Tone.Gilt : Tone.Bronze;
                DrawText(name, new(w - panelWidth - Margin + 16, y), TextSize, Theme.Of(tone));
                y += LineHeight;
            }
        }

        /// <summary>La scheda del proprio personaggio: quella del server se è arrivata, altrimenti quella del file.</summary>
        public void DrawSheet(SheetShown? sheet, Characters.Character? character)
        {
            var fields = sheet?.Fields.Select(f => (f.Key, f.Value)).ToList()
                         ?? character?.ToSheet().ToList()
                         ?? [];
            string title = sheet?.Owner ?? character?.Player ?? "";
            int h = Raylib.GetScreenHeight();
            float width = 460, x = Margin, y = 64;
            float height = 70 + Math.Max(1, fields.Count) * 25 + 8;
            Raylib.DrawRectangleRec(new(x, y, width, MathF.Min(height, h - y - 120)), Theme.WithAlpha(Theme.Panel, 230));
            Raylib.DrawRectangleLinesEx(new(x, y, width, MathF.Min(height, h - y - 120)), 1, Theme.PanelEdge);
            DrawCaps($"SCHEDA DI {title.ToUpperInvariant()}", new(x + 18, y + 16), 18, Theme.Of(Tone.Gilt));
            float row = y + 52;
            if (fields.Count == 0) DrawText("Ancora vuota: scrivi /imposta <campo> <valore>.", new(x + 18, row), 19, Theme.Of(Tone.Muted));
            foreach (var (key, value) in fields)
            {
                if (row > h - 150) break;
                DrawText(key, new(x + 18, row), 19, Theme.Of(Tone.Muted));
                var lines = Wrap(new StyledText().Add(value), width - 170, 19);
                foreach (var line in lines)
                {
                    DrawLine(line, new(x + 150, row), 19, 255);
                    row += 25;
                }
            }
        }

        // ---- Combattimento

        /// <summary>Nome, livello e salute sopra un nemico. Il bersaglio del mirino ha il nome dorato.</summary>
        public void DrawEnemyTag(EnemyView e, Vector2 screen, float distance, bool targeted)
        {
            float size = Math.Clamp(22f - distance * 0.6f, 13f, 20f);
            var label = e.Label;
            var measure = Raylib.MeasureTextEx(_caps, label, size, 1);
            float barWidth = MathF.Max(measure.X, 70);
            var at = new Vector2(screen.X - measure.X / 2, screen.Y - measure.Y - 10);
            Raylib.DrawRectangleRounded(new(screen.X - barWidth / 2 - 8, at.Y - 3, barWidth + 16, measure.Y + 16), 0.25f, 6, Theme.WithAlpha(Theme.Night, 160));
            if (targeted) Raylib.DrawRectangleRoundedLinesEx(new(screen.X - barWidth / 2 - 8, at.Y - 3, barWidth + 16, measure.Y + 16), 0.25f, 6, 1.5f, Theme.Of(Tone.Gilt));
            DrawCaps(label, at, size, targeted ? Theme.Of(Tone.Gilt) : e.Rank == "Normal" ? Theme.Of(Tone.Ink) : Theme.Of(Tone.Pompeian));
            float fill = e.MaxHealth == 0 ? 0 : (float)e.Health / e.MaxHealth;
            var bar = new Rectangle(screen.X - barWidth / 2, at.Y + measure.Y + 3, barWidth, 5);
            Raylib.DrawRectangleRec(bar, Theme.Basalt);
            Raylib.DrawRectangleRec(bar with { Width = bar.Width * fill }, Theme.Of(Tone.Pompeian));
        }

        public void DrawFloating(FloatingText f, Vector2 screen, double now)
        {
            float age = (float)(now - f.Time);
            byte alpha = (byte)(255 * Math.Clamp(1.4 - age, 0, 1));
            var at = new Vector2(screen.X - Measure(f.Text, 26) / 2, screen.Y - age * 40);
            Raylib.DrawTextEx(_text, f.Text, at + new Vector2(1.5f, 1.5f), 26, 0, Theme.WithAlpha(Theme.Night, alpha));
            Raylib.DrawTextEx(_text, f.Text, at, 26, 0, Theme.Of(f.Tone, alpha));
        }

        /// <summary>Salute ed esperienza in basso a destra.</summary>
        public void DrawVitals(Vitals v)
        {
            int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            float width = 330, x = w - Margin - width, y = h - Margin - 92;
            Raylib.DrawRectangleRec(new(x - 12, y - 10, width + 24, 72), Theme.WithAlpha(Theme.Panel, 170));
            DrawCaps($"LIVELLO {v.Level}", new(x, y - 2), 15, Theme.Of(Tone.Gilt));
            var health = $"{v.Health}/{v.MaxHealth}";
            DrawText(health, new(x + width - Measure(health, 18), y - 5), 18, Theme.Of(Tone.Ink));
            var hp = new Rectangle(x, y + 18, width, 12);
            Raylib.DrawRectangleRec(hp, Theme.Basalt);
            Raylib.DrawRectangleRec(hp with { Width = width * Math.Clamp((float)v.Health / Math.Max(1, v.MaxHealth), 0, 1) }, Theme.Of(Tone.Pompeian));
            Raylib.DrawRectangleLinesEx(hp, 1, Theme.PanelEdge);
            int from = LexArcana.Combat.Progression.ExperienceFor(v.Level), to = v.NextLevel;
            float xp = to <= from ? 1 : Math.Clamp((float)(v.Experience - from) / (to - from), 0, 1);
            var xpBar = new Rectangle(x, y + 38, width, 5);
            Raylib.DrawRectangleRec(xpBar, Theme.Basalt);
            Raylib.DrawRectangleRec(xpBar with { Width = width * xp }, Theme.Of(Tone.Gilt));
            var label = $"esperienza {v.Experience}/{to}";
            DrawText(label, new(x + width - Measure(label, 15), y + 44), 15, Theme.Of(Tone.Muted));
        }

        /// <summary>Un bordo rosso che sfuma: sei stato colpito.</summary>
        public static void DrawHurt(float strength)
        {
            int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight(), band = 120;
            var red = Theme.WithAlpha(Theme.Fresco, (byte)(180 * strength));
            var none = Theme.WithAlpha(Theme.Fresco, 0);
            Raylib.DrawRectangleGradientV(0, 0, w, band, red, none);
            Raylib.DrawRectangleGradientV(0, h - band, w, band, none, red);
            Raylib.DrawRectangleGradientH(0, 0, band, h, red, none);
            Raylib.DrawRectangleGradientH(w - band, 0, band, h, none, red);
        }

        public void DrawDown(double secondsLeft)
        {
            int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
            Raylib.DrawRectangle(0, 0, w, h, Theme.WithAlpha(Theme.Night, 150));
            const string title = "SEI A TERRA";
            DrawCaps(title, new(w / 2f - Raylib.MeasureTextEx(_caps, title, 40, 1).X / 2, h / 2f - 50), 40, Theme.Of(Tone.Pompeian));
            var hint = secondsLeft > 0 ? $"I compagni ti coprono: ti rialzi tra {Math.Ceiling(secondsLeft)} secondi." : "Ti rialzi…";
            DrawText(hint, new(w / 2f - Measure(hint, 22) / 2, h / 2f + 8), 22, Theme.Of(Tone.Ink));
        }

        private static readonly (string Keys, string What)[] Controls =
        [
            ("W A S D", "muoviti"), ("Clic sinistro", "colpisci in mischia"), ("Clic destro", "tira a distanza"), ("Maiusc", "corri"), ("Mouse", "guarda intorno"), ("Invio · T", "parla alla compagnia"),
            ("/", "scrivi un comando, es. /tira d20"), ("Tab", "chi è presente"), ("C", "la scheda del personaggio"), ("Esc", "libera o riprendi il mouse"), ("F1", "chiudi questo aiuto"),
        ];

        private void DrawHelp(int w, int h)
        {
            float panelWidth = 560, panelHeight = 120 + Controls.Length * 30 + 150;
            float x = w / 2f - panelWidth / 2, y = h / 2f - panelHeight / 2;
            Raylib.DrawRectangleRec(new(x, y, panelWidth, panelHeight), Theme.WithAlpha(Theme.Panel, 235));
            Raylib.DrawRectangleLinesEx(new(x, y, panelWidth, panelHeight), 1, Theme.PanelEdge);
            DrawCaps("COMANDI", new(x + 28, y + 24), 24, Theme.Of(Tone.Gilt));
            float row = y + 74;
            foreach (var (keys, what) in Controls)
            {
                DrawCaps(keys, new(x + 28, row + 3), 16, Theme.Of(Tone.Bronze));
                DrawText(what, new(x + 200, row), TextSize, Theme.Of(Tone.Ink));
                row += 30;
            }
            row += 12;
            foreach (var line in "Nella chat valgono gli stessi comandi del terminale:\n/tira, /segreto, /sussurra, /scheda, /imposta, /chi, /esci.\nIl Demiurgo vede tutti i tiri e i sussurri.".Split('\n'))
            {
                DrawText(line, new(x + 28, row), 19, Theme.Of(Tone.Muted));
                row += 25;
            }
        }

        private void DrawOverlay(string message, int w, int h)
        {
            Raylib.DrawRectangle(0, 0, w, h, Theme.WithAlpha(Theme.Night, 170));
            var lines = Wrap(new StyledText().Add(message, Tone.Ink), MathF.Min(700, w - 60), 26);
            float y = h / 2f - lines.Count * 34 / 2f;
            foreach (var line in lines)
            {
                float lw = line.Sum(p => Measure(p.Text, 26));
                DrawLine(line, new(w / 2f - lw / 2, y), 26, 255);
                y += 34;
            }
            const string hint = "Premi Esc per uscire";
            DrawText(hint, new(w / 2f - Measure(hint, 20) / 2, y + 18), 20, Theme.Of(Tone.Muted));
        }

        // ---- Testo

        [GeneratedRegex(@"\S+|\s+")]
        private static partial Regex Words { get; }

        /// <summary>Divide un testo a stili in righe larghe al massimo <paramref name="maxWidth"/> pixel, andando a capo tra le parole.</summary>
        private List<List<Span>> Wrap(StyledText text, float maxWidth, float size)
        {
            List<List<Span>> result = [];
            foreach (var source in text.Lines)
            {
                List<Span> line = [];
                float x = 0;
                foreach (var span in source)
                    foreach (Match word in Words.Matches(span.Text))
                    {
                        var piece = word.Value;
                        bool space = string.IsNullOrWhiteSpace(piece);
                        float pw = Measure(piece, size);
                        if (!space && x > 0 && x + pw > maxWidth)
                        {
                            result.Add(line);
                            line = [];
                            x = 0;
                        }
                        if (space && x == 0 && result.Count > 0 && line.Count == 0) continue;
                        if (line.Count > 0 && line[^1] is var last && last.Tone == span.Tone && last.Bold == span.Bold && last.Italic == span.Italic)
                            line[^1] = last with { Text = last.Text + piece };
                        else
                            line.Add(span with { Text = piece });
                        x += pw;
                    }
                result.Add(line);
            }
            return result;
        }

        private void DrawLine(List<Span> line, Vector2 at, float size, byte alpha)
        {
            foreach (var span in line)
            {
                var color = Theme.Of(span.Tone, alpha);
                var font = _text;
                // Nessuna variante corsiva o grassetta caricata: il grassetto si simula con un secondo passaggio spostato di un pixel.
                Raylib.DrawTextEx(font, span.Text, at + new Vector2(1, 1), size, 0, Theme.WithAlpha(Theme.Night, (byte)(alpha * 0.7)));
                Raylib.DrawTextEx(font, span.Text, at, size, 0, color);
                if (span.Bold) Raylib.DrawTextEx(font, span.Text, at + new Vector2(0.7f, 0), size, 0, color);
                at.X += Measure(span.Text, size);
            }
        }

        private void DrawText(string text, Vector2 at, float size, Color color) => Raylib.DrawTextEx(_text, text, at, size, 0, color);
        private void DrawCaps(string text, Vector2 at, float size, Color color) => Raylib.DrawTextEx(_caps, text, at, size, 1, color);
        private float Measure(string text, float size) => Raylib.MeasureTextEx(_text, text, size, 0).X;

        public void Dispose()
        {
            Raylib.UnloadFont(_text);
            Raylib.UnloadFont(_caps);
        }
    }
}

// ===== src/LexArcana.Visione/Lighting.cs =====
namespace LexArcana.Visione
{
    /// <summary>
    /// Lo shader della scena: luce ambiente, luna, fino a quattro luci puntiformi calde
    /// (la lanterna del personaggio, il fuoco dell'altare) e nebbia notturna.
    /// </summary>
    internal sealed class Lighting : IDisposable
    {
        public const int MaxLights = 4;

        private const string Vertex = """
            #version 330
            in vec3 vertexPosition;
            in vec3 vertexNormal;
            uniform mat4 mvp;
            uniform mat4 matModel;
            uniform mat4 matNormal;
            out vec3 fragPosition;
            out vec3 fragNormal;
            void main()
            {
                fragPosition = vec3(matModel * vec4(vertexPosition, 1.0));
                fragNormal = normalize(vec3(matNormal * vec4(vertexNormal, 0.0)));
                gl_Position = mvp * vec4(vertexPosition, 1.0);
            }
            """;

        private const string Fragment = """
            #version 330
            in vec3 fragPosition;
            in vec3 fragNormal;
            uniform vec4 colDiffuse;
            uniform vec3 viewPos;
            uniform vec3 ambient;
            uniform vec3 fogColor;
            uniform float fogDensity;
            uniform int lightCount;
            uniform vec3 lightPos[4];
            uniform vec3 lightColor[4];
            uniform float lightRange[4];
            uniform int pattern;      // 1 = lastre del pavimento
            uniform float emissive;   // 1 = brilla di luce propria (fuoco)
            out vec4 finalColor;

            void main()
            {
                vec3 n = normalize(fragNormal);
                vec3 base = colDiffuse.rgb;
                if (pattern == 1)
                {
                    vec2 cell = fragPosition.xz / 2.0;
                    vec2 f = fract(cell);
                    float tone = mod(floor(cell.x) + floor(cell.y), 2.0);
                    base *= mix(0.84, 1.0, tone);
                    float grout = step(0.965, max(f.x, f.y)) + step(min(f.x, f.y), 0.035);
                    base *= mix(1.0, 0.55, clamp(grout, 0.0, 1.0));
                }

                vec3 light = ambient;
                light += vec3(0.20, 0.22, 0.30) * max(dot(n, normalize(vec3(0.35, 1.0, 0.25))), 0.0);
                for (int i = 0; i < 4; i++)
                {
                    if (i >= lightCount) break;
                    vec3 d = lightPos[i] - fragPosition;
                    float dist = length(d);
                    float att = clamp(1.0 - dist / lightRange[i], 0.0, 1.0);
                    light += lightColor[i] * (0.25 + 0.75 * max(dot(n, d / max(dist, 0.001)), 0.0)) * att * att;
                }

                // compressione morbida dei toni: le zone molto illuminate non bruciano
                vec3 lit = vec3(1.0) - exp(-base * light * 1.4);
                vec3 color = mix(lit, base, emissive);
                float dist = length(viewPos - fragPosition);
                float fog = 1.0 - exp(-pow(dist * fogDensity, 2.0));
                finalColor = vec4(mix(color, fogColor, clamp(fog, 0.0, 1.0)), colDiffuse.a);
            }
            """;

        private readonly int _viewPos, _ambient, _fogColor, _fogDensity, _lightCount, _lightPos, _lightColor, _lightRange, _pattern, _emissive;

        public Lighting()
        {
            Shader = Raylib.LoadShaderFromMemory(Vertex, Fragment);
            _viewPos = Raylib.GetShaderLocation(Shader, "viewPos");
            _ambient = Raylib.GetShaderLocation(Shader, "ambient");
            _fogColor = Raylib.GetShaderLocation(Shader, "fogColor");
            _fogDensity = Raylib.GetShaderLocation(Shader, "fogDensity");
            _lightCount = Raylib.GetShaderLocation(Shader, "lightCount");
            _lightPos = Raylib.GetShaderLocation(Shader, "lightPos");
            _lightColor = Raylib.GetShaderLocation(Shader, "lightColor");
            _lightRange = Raylib.GetShaderLocation(Shader, "lightRange");
            _pattern = Raylib.GetShaderLocation(Shader, "pattern");
            _emissive = Raylib.GetShaderLocation(Shader, "emissive");

            Raylib.SetShaderValue(Shader, _ambient, new Vector3(0.26f, 0.22f, 0.20f), ShaderUniformDataType.Vec3);
            var fog = Theme.Fog;
            Raylib.SetShaderValue(Shader, _fogColor, new Vector3(fog.R / 255f, fog.G / 255f, fog.B / 255f), ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(Shader, _fogDensity, 0.042f, ShaderUniformDataType.Float);
            Pattern(false);
            Emissive(false);
        }

        public Shader Shader { get; }

        /// <summary>La distanza oltre la quale la nebbia copre tutto: inutile disegnare più in là.</summary>
        public const float VisibleDistance = 48f;

        public void Frame(Vector3 eye, ReadOnlySpan<(Vector3 Position, Vector3 Color, float Range)> lights)
        {
            Raylib.SetShaderValue(Shader, _viewPos, eye, ShaderUniformDataType.Vec3);
            int count = Math.Min(lights.Length, MaxLights);
            var positions = new float[MaxLights * 3];
            var colors = new float[MaxLights * 3];
            var ranges = new float[MaxLights];
            for (int i = 0; i < count; i++)
            {
                var (p, c, r) = lights[i];
                (positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]) = (p.X, p.Y, p.Z);
                (colors[i * 3], colors[i * 3 + 1], colors[i * 3 + 2]) = (c.X, c.Y, c.Z);
                ranges[i] = r;
            }
            Raylib.SetShaderValue(Shader, _lightCount, count, ShaderUniformDataType.Int);
            Raylib.SetShaderValueV(Shader, _lightPos, positions, ShaderUniformDataType.Vec3, MaxLights);
            Raylib.SetShaderValueV(Shader, _lightColor, colors, ShaderUniformDataType.Vec3, MaxLights);
            Raylib.SetShaderValueV(Shader, _lightRange, ranges, ShaderUniformDataType.Float, MaxLights);
        }

        public void Pattern(bool floor) => Raylib.SetShaderValue(Shader, _pattern, floor ? 1 : 0, ShaderUniformDataType.Int);
        public void Emissive(bool on) => Raylib.SetShaderValue(Shader, _emissive, on ? 1f : 0f, ShaderUniformDataType.Float);

        public void Dispose() => Raylib.UnloadShader(Shader);
    }
}

// ===== src/LexArcana.Visione/Theme.cs =====
namespace LexArcana.Visione
{
    /// <summary>
    /// I colori della scena, presi dai token del design system Lex Arcana:
    /// pietra e marmo dai toni vellum, il basalto per basi e notte, il rosso pompeiano per i fregi, il gilt per i capitelli.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Night = Hex(0x0c0a08);        // basalt (Night)
        public static readonly Color Fog = Hex(0x14100d);
        public static readonly Color Floor = Hex(0xcdbf9f);        // vellum-deep (Vellum), lastre di pietra
        public static readonly Color Stone = Hex(0xd8c8a6);        // muri
        public static readonly Color Marble = Hex(0xf4ecdc);       // vellum: colonne e statue
        public static readonly Color Basalt = Hex(0x26201c);       // basalt (Vellum): zoccoli, altare
        public static readonly Color Fresco = Hex(0x9a2b1e);       // pompeian (Vellum): fregi
        public static readonly Color Gilt = Hex(0xc4a058);
        public static readonly Color Flame = Hex(0xe8806a);        // pompeian (Night)

        public static readonly Color Panel = new(0x0c, 0x0a, 0x08, 0xc8);
        public static readonly Color PanelEdge = Hex(0x57493d);    // basalt-edge (Night)

        /// <summary>I colori delle tuniche: ogni nome ne riceve sempre lo stesso.</summary>
        private static readonly Color[] Tunics = [Hex(0xd3a95e), Hex(0x72bba8), Hex(0xe8806a), Hex(0x9a2b1e), Hex(0x2e6a5c), Hex(0x7d5a1c)];

        public static Color TunicFor(string name)
        {
            uint hash = 2166136261;
            foreach (var c in name.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
            return Tunics[hash % (uint)Tunics.Length];
        }

        public static Color Of(Tone tone, byte alpha = 255)
        {
            var (r, g, b) = Palette.Rgb(tone);
            return new Color(r, g, b, alpha);
        }

        public static Color WithAlpha(Color c, byte alpha) => new(c.R, c.G, c.B, alpha);

        private static Color Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255);
    }
}

// ===== src/LexArcana.Visione/VisioneApp.cs =====
namespace LexArcana.Visione
{
    /// <summary>
    /// La finestra 3D di un giocatore: il mondo visto con gli occhi del personaggio.
    /// La rete gira su un altro thread e consegna i messaggi in una coda; tutto il resto
    /// (input, movimento, disegno) avviene sul thread della finestra.
    /// </summary>
    internal sealed class VisioneApp(GameClient client, GameMap initialMap, VisioneOptions options) : IDisposable
    {
        private const float EyeHeight = 1.6f, WalkSpeed = 3.2f, RunSpeed = 5.6f, MouseSensitivity = 0.0028f;
        private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(100);

        private readonly ConcurrentQueue<ServerMessage> _inbox = new();
        private readonly Dictionary<string, Avatar> _avatars = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _present = [];
        private readonly MessageRenderer _renderer = new(client.Name);
        private GameMap _map = initialMap;
        private readonly Dictionary<int, EnemyView> _enemies = [];
        private readonly List<FloatingText> _floating = [];
        private readonly List<Shot> _shots = [];
        private Vitals? _vitals;
        private double _downSince, _hurtAt = -10, _swingAt = -10, _nextAttackAt;
        private int? _aimed;
        private const double LocalCooldown = 0.9, SwingTime = 0.32;
        private World? _world;
        private SheetShown? _mySheet;
        private bool _showSheet = options.ShowSheet;
        private Pose _pose;
        private Pose _lastSent;
        private float _pitch;
        private double _lastSentAt;
        // L'aiuto compare all'inizio e si chiude da solo al primo passo; aperto con F1 resta finché non lo si richiude.
        private bool _showHelp = true, _helpPinned, _mouseCaptured = true, _quit;
        private string? _overlay;

        public int Run()
        {
            var welcome = client.Welcome;
            _present.AddRange(welcome.Players);
            foreach (var p in welcome.Positions)
            {
                var pose = new Pose(p.X, p.Z, p.Yaw);
                if (p.Name.IsSameName(client.Name)) _pose = pose;
                else _avatars[p.Name] = new Avatar(p.Name, pose);
            }
            if (options.Frame is { } frame)
            {
                _pose = new Pose(frame.X, frame.Z, frame.Yaw);
                _pitch = frame.Pitch;
            }
            _lastSent = _pose;

            using var network = new CancellationTokenSource();
            var receiving = Task.Run(() => ReceiveAsync(network.Token));
            if (options.Character is { } character)
                client.Send(new Embody(character));

            Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
            Raylib.InitWindow(1280, 720, $"Lex Arcana · {client.Name}");
            Raylib.SetExitKey(KeyboardKey.Null);
            Raylib.SetTargetFPS(60);
            Raylib.DisableCursor();

            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            using (var lighting = new Lighting())
            using (var hud = new Hud(assets))
            {
                _world = new World(_map, lighting);
                var start = Raylib.GetTime();
                hud.Add(_renderer.Render(welcome)!, 0);
                bool captured = false;
                var lights = new (Vector3, Vector3, float)[Lighting.MaxLights];

                while (!_quit && !Raylib.WindowShouldClose())
                {
                    double now = Raylib.GetTime() - start;
                    float dt = MathF.Min(Raylib.GetFrameTime(), 0.1f);

                    Drain(hud, lighting, now);
                    var world = _world!;
                    HandleInput(hud, dt, now);
                    foreach (var avatar in _avatars.Values) avatar.Update(dt);
                    foreach (var enemy in _enemies.Values) enemy.Update(dt);
                    foreach (var gone in _enemies.Values.Where(e => e.DiedAt is { } d && now - d > 1.3).Select(e => e.Id).ToList()) _enemies.Remove(gone);
                    _floating.RemoveAll(f => now - f.Time > 1.4);
                    _shots.RemoveAll(s => now - s.Time > 0.2);
                    if (_showHelp && (now > 12 || _pose.DistanceTo(_lastSent) > 0.05f || hud.Typing || options.CapturePath is not null)) _showHelp = _helpPinned;
                    SendPose(now);

                    var forward = new Vector3(MathF.Sin(_pose.Yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_pose.Yaw) * MathF.Cos(_pitch));
                    var eye = new Vector3(_pose.X, EyeHeight, _pose.Z);
                    var camera = new Camera3D(eye, eye + forward, Vector3.UnitY, 70f, CameraProjection.Perspective);
                    _aimed = Aim(eye, forward);
                    if (_overlay is null && !hud.Typing && _vitals?.Down != true) HandleAttack(eye, forward, now);

                    int count = 0;
                    lights[count++] = (eye + new Vector3(0, -0.2f, 0) + forward * 0.3f, new Vector3(0.62f, 0.5f, 0.36f), 9f); // lanterna
                    foreach (var fire in world.Fires(now).Take(Lighting.MaxLights - 1)) lights[count++] = fire;
                    lighting.Frame(eye, lights.AsSpan(0, count));

                    Raylib.BeginDrawing();
                    Raylib.ClearBackground(Theme.Night);
                    Raylib.BeginMode3D(camera);
                    world.Draw(eye, now);
                    foreach (var avatar in _avatars.Values) world.DrawAvatar(avatar);
                    foreach (var enemy in _enemies.Values) world.DrawEnemy(enemy, now);
                    foreach (var shot in _shots) world.DrawShot(shot, now);
                    if (_vitals?.Down != true)
                    {
                        float swing = (float)Math.Clamp((now - _swingAt) / SwingTime, 0, 1);
                        world.DrawWeapon(eye, _pose.Yaw, _pitch, swing >= 1 ? 0 : swing, (float)now * 1.7f);
                    }
                    Raylib.EndMode3D();

                    DrawEnemyTags(hud, camera, eye, forward);
                    foreach (var f in _floating) hud.DrawFloating(f, Raylib.GetWorldToScreen(f.Position, camera), now);
                    if (now - _hurtAt < 0.5) Hud.DrawHurt((float)(1 - (now - _hurtAt) / 0.5));
                    if (_vitals is { } myVitals) hud.DrawVitals(myVitals);

                    DrawNameTags(hud, camera, eye, forward);
                    hud.DrawPlaceName(_map.Name);
                    if (_showSheet || Raylib.IsKeyDown(KeyboardKey.C) && !hud.Typing) hud.DrawSheet(_mySheet, options.Character);
                    if (_overlay is null && !hud.Typing && !_showHelp) Hud.DrawCrosshair();
                    if (_vitals?.Down == true && _overlay is null) hud.DrawDown(CombatRespawnDelay - (now - _downSince));
                    hud.Draw(now, _showHelp, _present, _overlay);
                    Raylib.EndDrawing();

                    if (options.CapturePath is { } path && !captured && now >= options.CaptureAfter)
                    {
                        var image = Raylib.LoadImageFromScreen();
                        Raylib.ExportImage(image, path);
                        Raylib.UnloadImage(image);
                        captured = true;
                        _quit = true;
                    }
                }
                Dispose(); // la scena va liberata finché la finestra e il contesto OpenGL esistono
            }

            Raylib.CloseWindow();
            SaveExperience();
            network.Cancel();
            try { receiving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            return 0;
        }

        public void Dispose()
        {
            _world?.Dispose();
            _world = null;
        }

        private async Task ReceiveAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var message in client.ReadAllAsync(ct)) _inbox.Enqueue(message);
                _inbox.Enqueue(new Expelled("La connessione con il Demiurgo si è interrotta."));
            }
            catch (OperationCanceledException) { }
            catch (ProtocolException ex)
            {
                _inbox.Enqueue(new Expelled(ex.Message));
            }
        }

        private void Drain(Hud hud, Lighting lighting, double now)
        {
            while (_inbox.TryDequeue(out var message))
            {
                switch (message)
                {
                    case MapChanged(var data, var positions, _):
                        if (GameMap.TryParse([$"nome: {data.Name}", .. data.Rows], out var newMap, out var mapError))
                        {
                            _world?.Dispose();
                            _map = newMap;
                            _world = new World(newMap, lighting);
                            _avatars.Clear();
                            foreach (var p in positions)
                            {
                                var pose = new Pose(p.X, p.Z, p.Yaw);
                                if (p.Name.IsSameName(client.Name)) { _pose = pose; _lastSent = pose; _pitch = 0; }
                                else _avatars[p.Name] = new Avatar(p.Name, pose);
                            }
                        }
                        else hud.Add(new StyledText().Add("! Il Demiurgo ha mandato una mappa non valida: " + mapError, Tone.Pompeian), now);
                        break;
                    case EnemyList(var list):
                        _enemies.Clear();
                        foreach (var info in list) _enemies[info.Id] = new EnemyView(info);
                        break;
                    case EnemyMoves(var poses):
                        foreach (var pose in poses)
                            if (_enemies.TryGetValue(pose.Id, out var view)) { view.Target = new(pose.X, pose.Z); view.TargetYaw = pose.Yaw; }
                        break;
                    case Struck s:
                        OnStruck(s, now);
                        break;
                    case Vitals v when v.Name.IsSameName(client.Name):
                        if (v.Down && _vitals?.Down != true) _downSince = now;
                        _vitals = v;
                        continue;
                    case SheetShown sheet when sheet.Owner.IsSameName(client.Name):
                        _mySheet = sheet;
                        if (sheet.UpdatedBy?.IsSameName(client.Name) == true) continue; // la propria compilazione non riempie la chat
                        break;
                    case Moved(var name, var x, var z, var yaw) when name.IsSameName(client.Name):
                        _pose = new Pose(x, z, _pose.Yaw); // il server ha corretto una mossa non valida
                        _lastSent = _pose;
                        break;
                    case Moved(var name, var x, var z, var yaw):
                        if (_avatars.TryGetValue(name, out var avatar)) avatar.Target = new Pose(x, z, yaw);
                        else _avatars[name] = new Avatar(name, new Pose(x, z, yaw));
                        break;
                    case Joined(var name):
                        if (!_present.Contains(name, StringComparer.OrdinalIgnoreCase)) _present.Add(name);
                        break;
                    case Left(var name):
                        _avatars.Remove(name);
                        _present.RemoveAll(n => n.IsSameName(name));
                        break;
                    case PlayerList(var names):
                        _present.Clear();
                        _present.AddRange(names);
                        break;
                    case Narrated(var text):
                        hud.Banner(text.Sanitized, now);
                        break;
                    case Expelled(var reason):
                        _overlay ??= reason.Sanitized is { Length: > 0 } r ? r : "Il Demiurgo ti ha allontanato dalla sessione.";
                        break;
                }
                if (_renderer.Render(message) is { } styled && message is not Expelled) hud.Add(styled, now);
            }
        }

        private void HandleInput(Hud hud, float dt, double now)
        {
            if (_vitals?.Down == true) return; // a terra non ci si muove
            if (_overlay is not null)
            {
                Raylib.EnableCursor();
                if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.Enter)) _quit = true;
                return;
            }

            if (hud.Typing)
            {
                if (hud.UpdateInput() is { } line) Submit(hud, line, now);
                return;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.F1)) _showHelp = _helpPinned = !_showHelp;
            if (Raylib.IsKeyPressed(KeyboardKey.Tab) && Raylib.IsKeyDown(KeyboardKey.LeftShift)) _showSheet = !_showSheet;
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.T)) hud.OpenInput();
            else if (Raylib.IsKeyPressed(KeyboardKey.Slash)) hud.OpenInput("/");
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                _mouseCaptured = !_mouseCaptured;
                if (_mouseCaptured) Raylib.DisableCursor(); else Raylib.EnableCursor();
            }
            if (!_mouseCaptured && Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                _nextAttackAt = now + 0.2; // il clic che riprende il mouse non è un colpo
                _mouseCaptured = true;
                Raylib.DisableCursor();
            }
            if (hud.Typing) return;

            if (options.AutoFight)
            {
                AutoFight(dt);
                return;
            }

            float yaw = _pose.Yaw;
            if (_mouseCaptured)
            {
                var mouse = Raylib.GetMouseDelta();
                yaw -= mouse.X * MouseSensitivity;
                _pitch = Math.Clamp(_pitch - mouse.Y * MouseSensitivity, -1.35f, 1.35f);
            }
            if (Raylib.IsKeyDown(KeyboardKey.Left)) yaw += 2.2f * dt;
            if (Raylib.IsKeyDown(KeyboardKey.Right)) yaw -= 2.2f * dt;

            var forward = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
            var right = new Vector2(-forward.Y, forward.X);
            var move = Vector2.Zero;
            if (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up)) move += forward;
            if (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down)) move -= forward;
            if (Raylib.IsKeyDown(KeyboardKey.D)) move += right;
            if (Raylib.IsKeyDown(KeyboardKey.A)) move -= right;
            if (move != Vector2.Zero)
            {
                float speed = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift) ? RunSpeed : WalkSpeed;
                move = Vector2.Normalize(move) * speed * dt;
            }
            _pose = _map.Slide(_pose, move.X, move.Y, yaw);
        }

        private void Submit(Hud hud, string line, double now)
        {
            switch (PlayerCommands.Parse(line))
            {
                case PlayerInput.Send(var message):
                    if (!client.Send(message)) _overlay = "La connessione con il Demiurgo si è interrotta.";
                    break;
                case PlayerInput.ShowHelp:
                    _showHelp = _helpPinned = true;
                    break;
                case PlayerInput.Invalid(var reason):
                    hud.Add(new StyledText().Add("! " + reason, Tone.Pompeian), now);
                    break;
                case PlayerInput.Quit:
                    _quit = true;
                    break;
            }
        }

        private const double CombatRespawnDelay = LexArcana.Combat.CombatEngine.RespawnDelay;

        /// <summary>Il nemico più vicino sotto il mirino, se c'è e si vede.</summary>
        private int? Aim(Vector3 eye, Vector3 forward)
        {
            int? best = null;
            float bestDistance = float.MaxValue;
            foreach (var e in _enemies.Values)
            {
                if (e.DiedAt is not null) continue;
                var center = new Vector3(e.Position.X, e.Height * 0.55f, e.Position.Y);
                var to = center - eye;
                float distance = to.Length();
                if (distance > LexArcana.Combat.Fighter.RangedReach || distance < 0.01f) continue;
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(to / distance, forward), -1, 1));
                if (angle > MathF.Atan(0.75f * e.Scale / distance) + 0.05f) continue;
                if (!_map.HasLineOfSight(eye.X, eye.Z, e.Position.X, e.Position.Y)) continue;
                if (distance < bestDistance) { best = e.Id; bestDistance = distance; }
            }
            return best;
        }

        /// <summary>Solo per le prove automatiche: guarda il nemico più vicino che si vede e gli va incontro.</summary>
        private void AutoFight(float dt)
        {
            var me = new Vector2(_pose.X, _pose.Z);
            var target = _enemies.Values.Where(e => e.DiedAt is null && _map.HasLineOfSight(me.X, me.Y, e.Position.X, e.Position.Y))
                .OrderBy(e => Vector2.Distance(me, e.Position)).FirstOrDefault();
            if (target is null) return;
            var to = target.Position - me;
            float distance = to.Length();
            float yaw = MathF.Atan2(to.X, to.Y);
            _pitch = MathF.Atan2(target.Height * 0.55f - EyeHeight, distance) * 0.8f;
            var step = distance > 1.8f ? Vector2.Normalize(to) * WalkSpeed * dt : Vector2.Zero;
            _pose = _map.Slide(_pose, step.X, step.Y, yaw);
            _autoRanged = distance > LexArcana.Combat.Fighter.MeleeReach;
            _autoAttack = true;
        }

        private bool _autoAttack, _autoRanged;

        private void HandleAttack(Vector3 eye, Vector3 forward, double now)
        {
            if (!(_mouseCaptured || options.AutoFight) || now < _nextAttackAt) return;
            bool melee = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.F) || (_autoAttack && !_autoRanged);
            bool ranged = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.R) || (_autoAttack && _autoRanged);
            _autoAttack = false;
            if (!melee && !ranged) return;
            _nextAttackAt = now + LocalCooldown;
            if (melee) _swingAt = now;
            if (_aimed is { } id && _enemies.TryGetValue(id, out var target))
            {
                if (ranged) _shots.Add(new Shot(eye + forward * 0.5f - Vector3.UnitY * 0.15f, new Vector3(target.Position.X, target.Height * 0.55f, target.Position.Y), now));
                client.Send(new Attack(id, ranged));
            }
            else if (ranged) _shots.Add(new Shot(eye + forward * 0.5f, eye + forward * 14f, now));
        }

        private void OnStruck(Struck s, double now)
        {
            if (s.TargetEnemyId is { } enemyId && _enemies.TryGetValue(enemyId, out var enemy))
            {
                enemy.Health = s.Health;
                var head = new Vector3(enemy.Position.X, enemy.Height + 0.1f, enemy.Position.Y);
                if (s.Hit)
                {
                    enemy.FlashUntil = now + 0.15;
                    _floating.Add(new FloatingText(head, s.Critical ? $"{s.Damage}!" : s.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Critical ? Tone.Gilt : Tone.Ink, now));
                }
                else _floating.Add(new FloatingText(head, "mancato", Tone.Muted, now));
                if (s.Defeated) enemy.DiedAt = now;
            }
            if (s.AttackerEnemyId is { } attackerId && _enemies.TryGetValue(attackerId, out var attacker)) attacker.AttackAt = now;
            if (s.TargetEnemyId is null && s.Target.IsSameName(client.Name) && s.Hit) _hurtAt = now;
            if (s.TargetEnemyId is null && !s.Target.IsSameName(client.Name) && _avatars.TryGetValue(s.Target, out var ally) && s.Hit)
                _floating.Add(new FloatingText(new Vector3(ally.Position.X, 2.0f, ally.Position.Y), s.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone.Pompeian, now));
        }

        private void DrawEnemyTags(Hud hud, Camera3D camera, Vector3 eye, Vector3 forward)
        {
            foreach (var e in _enemies.Values.Where(e => e.DiedAt is null).OrderByDescending(e => Vector2.Distance(e.Position, new(eye.X, eye.Z))))
            {
                var head = new Vector3(e.Position.X, e.Height + 0.25f, e.Position.Y);
                var to = head - eye;
                float distance = to.Length();
                if (distance > 24 || Vector3.Dot(to, forward) <= 0.2f) continue;
                if (!_map.HasLineOfSight(eye.X, eye.Z, e.Position.X, e.Position.Y)) continue;
                hud.DrawEnemyTag(e, Raylib.GetWorldToScreen(head, camera), distance, e.Id == _aimed);
            }
        }

        /// <summary>All'uscita, scrive nel file del personaggio l'esperienza guadagnata.</summary>
        private void SaveExperience()
        {
            if (options.Character is not { } character || options.CharacterPath is not { } path || _vitals is not { } v) return;
            if (v.Experience <= character.Experience) return;
            try
            {
                File.WriteAllText(path, (character with { Experience = v.Experience }).ToJson());
                Console.WriteLine($"Esperienza salvata in {path}: {v.Experience} (livello {v.Level}).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Impossibile salvare l'esperienza in {path}: {ex.Message}");
            }
        }

        /// <summary>Manda la posizione al server al massimo 10 volte al secondo, e solo se è cambiata.</summary>
        private void SendPose(double now)
        {
            if (_overlay is not null || options.Frame is not null || now - _lastSentAt < MoveInterval.TotalSeconds) return;
            if (_pose.DistanceTo(_lastSent) < 0.01f && MathF.Abs(MathF.IEEERemainder(_pose.Yaw - _lastSent.Yaw, MathF.Tau)) < 0.01f) return;
            client.Send(new Move(_pose.X, _pose.Z, _pose.Yaw));
            _lastSent = _pose;
            _lastSentAt = now;
        }

        private void DrawNameTags(Hud hud, Camera3D camera, Vector3 eye, Vector3 forward)
        {
            foreach (var avatar in _avatars.Values.OrderByDescending(a => Vector2.Distance(a.Position, new(eye.X, eye.Z))))
            {
                var head = new Vector3(avatar.Position.X, 1.82f, avatar.Position.Y);
                var toHead = head - eye;
                float distance = toHead.Length();
                if (distance > 30 || Vector3.Dot(toHead, forward) <= 0.2f) continue;
                if (!_map.HasLineOfSight(eye.X, eye.Z, avatar.Position.X, avatar.Position.Y)) continue;
                hud.DrawNameTag(avatar.Name, Raylib.GetWorldToScreen(head, camera), distance);
            }
        }
    }
}

// ===== src/LexArcana.Visione/VisioneOptions.cs =====
namespace LexArcana.Visione
{
    internal sealed record VisioneOptions(string Host, int Port, string Name, string? Password, string? CapturePath, float CaptureAfter, (float X, float Z, float Yaw, float Pitch)? Frame = null, Character? Character = null, bool ShowSheet = false, string? CharacterPath = null, bool AutoFight = false)
    {
        public const string Usage = """
            lexarcana-visione --personaggio <file.json> | --nome <nome> [--host 127.0.0.1] [--porta 7777] [--parola-d-ordine <testo>]
                Entra nella sessione di un Demiurgo e guarda il mondo con gli occhi del tuo personaggio.
                Il personaggio si crea con: lexarcana personaggio --nome <nome>

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
}

// ===== src/LexArcana.Visione/World.cs =====
namespace LexArcana.Visione
{
    /// <summary>Disegna il luogo (pavimento, muri, colonne, altare) e i personaggi.</summary>
    internal sealed unsafe class World : IDisposable
    {
        private readonly GameMap _map;
        private readonly Lighting _lighting;
        private readonly Model _cube, _cylinder, _sphere, _cone;
        private readonly List<(int X, int Z)> _walls = [], _columns = [], _altars = [];

        public World(GameMap map, Lighting lighting)
        {
            _map = map;
            _lighting = lighting;
            _cube = Load(Raylib.GenMeshCube(1, 1, 1));
            _cylinder = Load(Raylib.GenMeshCylinder(1, 1, 28));
            _sphere = Load(Raylib.GenMeshSphere(1, 18, 18));
            _cone = Load(Raylib.GenMeshCone(1, 1, 20));

            for (int x = 0; x < map.Width; x++)
                for (int z = 0; z < map.Depth; z++)
                    switch (map[x, z])
                    {
                        // Un muro circondato da altri muri non si vede mai: non lo disegniamo.
                        case Tile.Wall when TouchesOpenSpace(x, z): _walls.Add((x, z)); break;
                        case Tile.Column: _columns.Add((x, z)); break;
                        case Tile.Altar: _altars.Add((x, z)); break;
                    }
        }

        /// <summary>Le luci fisse del luogo: un fuoco su ogni altare, che tremola.</summary>
        public IEnumerable<(Vector3 Position, Vector3 Color, float Range)> Fires(double time)
        {
            foreach (var (x, z) in _altars)
            {
                var (cx, cz) = GameMap.CellCenter(x, z);
                float flicker = 0.85f + 0.1f * MathF.Sin((float)time * 9.1f + x) + 0.05f * MathF.Sin((float)time * 23.7f + z);
                yield return (new Vector3(cx, 1.8f, cz), new Vector3(1.15f, 0.55f, 0.28f) * flicker, 16f);
            }
        }

        public void Draw(Vector3 eye, double time)
        {
            float s = GameMap.CellSize, h = GameMap.WallHeight;

            // Pavimento: una sola lastra, le fughe le disegna lo shader.
            _lighting.Pattern(true);
            Box(new(_map.Width * s / 2, -0.1f, _map.Depth * s / 2), new(_map.Width * s, 0.2f, _map.Depth * s), Theme.Floor);
            _lighting.Pattern(false);

            foreach (var (x, z) in _walls)
            {
                var (cx, cz) = GameMap.CellCenter(x, z);
                if (!Near(eye, cx, cz)) continue;
                Box(new(cx, 0.15f, cz), new(s, 0.3f, s), Theme.Basalt);                    // zoccolo
                Box(new(cx, 0.3f + (h - 0.8f) / 2, cz), new(s, h - 0.8f, s), Theme.Stone);  // parete
                Box(new(cx, h - 0.375f, cz), new(s + 0.02f, 0.25f, s + 0.02f), Theme.Fresco); // fregio
                Box(new(cx, h - 0.125f, cz), new(s + 0.1f, 0.25f, s + 0.1f), Theme.Stone);  // cornice
            }

            foreach (var (x, z) in _columns)
            {
                var (cx, cz) = GameMap.CellCenter(x, z);
                if (!Near(eye, cx, cz)) continue;
                Box(new(cx, 0.1f, cz), new(1.0f, 0.2f, 1.0f), Theme.Marble);
                Box(new(cx, 0.26f, cz), new(0.82f, 0.12f, 0.82f), Theme.Marble);
                Cylinder(new(cx, 0.32f, cz), 0.32f, h - 0.82f, Theme.Marble);
                Box(new(cx, h - 0.4f, cz), new(0.78f, 0.16f, 0.78f), Theme.Gilt);          // capitello
                Box(new(cx, h - 0.24f, cz), new(1.0f, 0.16f, 1.0f), Theme.Marble);         // abaco
            }

            foreach (var (x, z) in _altars)
            {
                var (cx, cz) = GameMap.CellCenter(x, z);
                if (!Near(eye, cx, cz)) continue;
                Box(new(cx, 0.1f, cz), new(1.9f, 0.2f, 1.9f), Theme.Marble);
                Box(new(cx, 0.6f, cz), new(1.4f, 0.8f, 1.4f), Theme.Basalt);
                Box(new(cx, 1.05f, cz), new(1.65f, 0.1f, 1.65f), Theme.Gilt);
                float t = (float)time;
                float lick = 1f + 0.12f * MathF.Sin(t * 11f) + 0.06f * MathF.Sin(t * 27f);
                _lighting.Emissive(true);
                for (int i = 0; i < 5; i++)
                {
                    // cinque lingue di fuoco che tremolano ognuna per conto suo
                    float a = i * MathF.Tau / 5 + t * 0.7f;
                    var offset = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 0.16f;
                    float tongue = 0.35f + 0.18f * MathF.Sin(t * (7f + i * 1.7f) + i * 2.1f);
                    Cone(new Vector3(cx, 1.1f, cz) + offset, 0.11f, tongue * lick, t * 30f + i * 40f, Theme.Flame);
                }
                Cone(new(cx, 1.1f, cz), 0.17f, 0.5f * (2f - lick), -t * 70f, Theme.Gilt);
                Sphere(new(cx, 1.14f, cz), 0.16f, Theme.Flame);
                _lighting.Emissive(false);
            }
        }

        /// <summary>Un personaggio: una statua di marmo con la tunica del suo colore e una fascia scura sugli occhi che dice dove guarda.</summary>
        public void DrawAvatar(Avatar avatar)
        {
            var p = new Vector3(avatar.Position.X, 0, avatar.Position.Y);
            float angle = avatar.Yaw * 180f / MathF.PI;
            var forward = new Vector3(MathF.Sin(avatar.Yaw), 0, MathF.Cos(avatar.Yaw));
            float bob = MathF.Abs(MathF.Sin(avatar.Phase)) * 0.05f * avatar.Stride;
            var tunic = Theme.TunicFor(avatar.Name);

            Cylinder(p, 0.34f, 0.1f, Theme.Basalt);                                            // piedistallo
            Cone(p + new Vector3(0, 0.1f + bob, 0), 0.34f, 1.45f, angle, tunic);               // veste, stretta in alto: la punta resta dentro la testa
            Cylinder(p + new Vector3(0, 0.1f + bob, 0), 0.335f, 0.08f, Theme.Gilt);            // orlo
            Rotated(p + new Vector3(0, 1.2f + bob, 0), new(0.5f, 0.14f, 0.24f), angle, tunic); // spalle
            Rotated(p + new Vector3(0, 1.0f + bob, 0) + forward * 0.06f, new(0.08f, 0.5f, 0.2f), angle - 35f, Theme.Marble); // braccio e mantello
            Cylinder(p + new Vector3(0, 1.27f + bob, 0), 0.07f, 0.12f, Theme.Marble);          // collo
            Sphere(p + new Vector3(0, 1.5f + bob, 0), 0.15f, Theme.Marble);                    // testa
            Rotated(p + new Vector3(0, 1.53f + bob, 0) + forward * 0.12f, new(0.2f, 0.04f, 0.07f), angle, Theme.Basalt); // occhi
            Cylinder(p + new Vector3(0, 1.6f + bob, 0), 0.155f, 0.045f, Theme.Gilt);           // corona d'alloro
        }

        /// <summary>Un nemico, disegnato secondo il suo tipo. Lampeggia di bianco se è appena stato colpito e affonda quando muore.</summary>
        public void DrawEnemy(EnemyView e, double time)
        {
            float t = (float)time;
            float dying = e.DiedAt is { } died ? (float)Math.Clamp((time - died) / 1.2, 0, 1) : 0;
            float scale = e.Scale * (1f - dying * 0.4f);
            var p = new Vector3(e.Position.X, -dying * 1.2f, e.Position.Y);
            bool flash = time < e.FlashUntil;
            Color C(Color c) => flash ? Theme.Marble : c;
            float lunge = time - e.AttackAt < 0.35 ? MathF.Sin((float)(time - e.AttackAt) / 0.35f * MathF.PI) * 0.35f : 0f;
            float bob = MathF.Abs(MathF.Sin(e.Phase)) * 0.06f * e.Stride;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Yaw);
            Vector3 L(float x, float y, float z) => p + Vector3.Transform(new Vector3(x, y, z + lunge) * scale, q);
            void Part(Vector3 center, Vector3 size, Color color, Quaternion? extra = null) =>
                Oriented(_cube, center, extra is { } r ? Quaternion.Concatenate(r, q) : q, size * scale, C(color));

            switch (e.Kind)
            {
                case "lupo":
                {
                    float gait = MathF.Sin(e.Phase * 2.2f) * 0.25f * e.Stride;
                    Part(L(0, 0.55f + bob, 0), new(0.38f, 0.42f, 0.95f), Theme.Basalt);
                    Part(L(0, 0.72f + bob, 0.6f), new(0.28f, 0.3f, 0.38f), Theme.Basalt);
                    Part(L(0, 0.66f + bob, 0.85f), new(0.16f, 0.14f, 0.22f), Theme.Basalt);          // muso
                    Part(L(0, 0.6f, -0.55f), new(0.08f, 0.08f, 0.4f), Theme.Basalt, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f)); // coda
                    foreach (var (lx, lz, phase) in new[] { (0.14f, 0.32f, 1f), (-0.14f, 0.32f, -1f), (0.14f, -0.32f, -1f), (-0.14f, -0.32f, 1f) })
                        Part(L(lx, 0.22f, lz + gait * phase), new(0.09f, 0.44f, 0.09f), Theme.Basalt);
                    _lighting.Emissive(true);
                    Sphere(L(0.08f, 0.8f + bob, 0.8f), 0.035f * scale, Theme.Flame);
                    Sphere(L(-0.08f, 0.8f + bob, 0.8f), 0.035f * scale, Theme.Flame);
                    _lighting.Emissive(false);
                    break;
                }
                case "strige":
                {
                    float hover = 1.7f + MathF.Sin(t * 2.1f + e.Id) * 0.15f;
                    float flap = MathF.Sin(t * 14f + e.Id) * 0.7f;
                    Sphere(L(0, hover, 0), 0.22f * scale, C(Theme.Basalt));
                    Sphere(L(0, hover + 0.25f, 0.12f), 0.14f * scale, C(Theme.Marble));                // volto pallido
                    Part(L(0.38f, hover + 0.05f, 0), new(0.6f, 0.03f, 0.3f), Theme.Fresco, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, flap));
                    Part(L(-0.38f, hover + 0.05f, 0), new(0.6f, 0.03f, 0.3f), Theme.Fresco, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -flap));
                    Part(L(0.06f, hover - 0.28f, 0.05f), new(0.04f, 0.18f, 0.04f), Theme.Gilt);       // artigli di bronzo
                    Part(L(-0.06f, hover - 0.28f, 0.05f), new(0.04f, 0.18f, 0.04f), Theme.Gilt);
                    break;
                }
                case "scheletro":
                {
                    float gait = MathF.Sin(e.Phase * 2f) * 0.2f * e.Stride;
                    Part(L(0.12f, 0.45f, gait), new(0.07f, 0.9f, 0.07f), Theme.Marble);
                    Part(L(-0.12f, 0.45f, -gait), new(0.07f, 0.9f, 0.07f), Theme.Marble);
                    Part(L(0, 1.25f + bob, 0), new(0.4f, 0.55f, 0.18f), Theme.Marble);              // costole
                    Part(L(0, 0.98f + bob, 0), new(0.36f, 0.08f, 0.2f), Theme.Fresco);              // cintura rossa
                    Sphere(L(0, 1.72f + bob, 0), 0.15f * scale, C(Theme.Marble));
                    Part(L(0, 1.85f + bob, 0), new(0.34f, 0.08f, 0.34f), Theme.Gilt);               // elmo
                    Part(L(0, 1.98f + bob, 0), new(0.04f, 0.16f, 0.3f), Theme.Fresco);              // cresta
                    Part(L(-0.3f, 1.2f + bob, 0.18f), new(0.08f, 0.75f, 0.5f), Theme.Fresco);       // scudo
                    Part(L(0.28f, 1.2f + bob, 0.3f), new(0.05f, 0.05f, 0.7f), Theme.Marble);        // gladio
                    break;
                }
                case "lemure":
                {
                    float hover = 0.25f + MathF.Sin(t * 1.6f + e.Id) * 0.1f;
                    _lighting.Emissive(true);
                    Cone(L(0, hover, 0), 0.4f * scale, 1.7f * scale, e.Yaw * 57.3f, C(Theme.WithAlpha(Theme.Marble, 200)));
                    Sphere(L(0, hover + 1.45f, 0), 0.19f * scale, C(Theme.WithAlpha(Theme.Marble, 220)));
                    _lighting.Emissive(false);
                    Part(L(0.07f, hover + 1.48f, 0.16f), new(0.05f, 0.07f, 0.03f), Theme.Basalt);
                    Part(L(-0.07f, hover + 1.48f, 0.16f), new(0.05f, 0.07f, 0.03f), Theme.Basalt);
                    Part(L(0, hover + 1.36f, 0.17f), new(0.09f, 0.03f, 0.02f), Theme.Basalt);
                    break;
                }
                case "custode":
                {
                    var bronze = new Color((byte)0x9a, (byte)0x6a, (byte)0x2a, (byte)255);
                    float gait = MathF.Sin(e.Phase * 1.6f) * 0.15f * e.Stride;
                    Part(L(0.22f, 0.5f, gait), new(0.26f, 1.0f, 0.3f), bronze);
                    Part(L(-0.22f, 0.5f, -gait), new(0.26f, 1.0f, 0.3f), bronze);
                    Part(L(0, 1.45f + bob, 0), new(0.95f, 0.95f, 0.55f), bronze);
                    Part(L(0.62f, 1.35f + bob, lunge * 2), new(0.25f, 0.9f, 0.28f), bronze);
                    Part(L(-0.62f, 1.35f + bob, 0), new(0.25f, 0.9f, 0.28f), bronze);
                    Part(L(0, 2.15f + bob, 0), new(0.45f, 0.42f, 0.42f), bronze);
                    Part(L(0, 1.45f + bob, 0.29f), new(0.5f, 0.12f, 0.02f), Theme.Gilt);
                    _lighting.Emissive(true);
                    Part(L(0, 2.18f + bob, 0.22f), new(0.3f, 0.06f, 0.02f), Theme.Flame);           // occhi di brace
                    _lighting.Emissive(false);
                    break;
                }
                default: // cultista, ladro di tombe: figure incappucciate
                {
                    bool thief = e.Kind == "ladro";
                    var robe = thief ? new Color((byte)0x5c, (byte)0x4d, (byte)0x40, (byte)255) : Theme.Basalt;
                    float h = thief ? 1.3f : 1.5f;
                    Cone(L(0, 0.05f + bob, 0), 0.36f * scale, h * scale, e.Yaw * 57.3f, C(robe));
                    Part(L(0, h - 0.25f + bob, 0), new(0.5f, 0.14f, 0.26f), robe);
                    Sphere(L(0, h + 0.02f + bob, 0), 0.17f * scale, C(thief ? robe : Theme.Fresco)); // cappuccio
                    Part(L(0, h + bob, 0.13f), new(0.18f, 0.12f, 0.03f), Theme.Night);              // il volto nell'ombra
                    Part(L(0.3f, h - 0.4f + bob, 0.25f), new(0.04f, 0.04f, 0.42f), Theme.Gilt,
                        Quaternion.CreateFromAxisAngle(Vector3.UnitX, -1.2f * lunge * 3));           // falcetto o pugnale
                    break;
                }
            }
        }

        /// <summary>
        /// La spada in prima persona, davanti alla telecamera. <paramref name="swing"/> va da 0 a 1 durante un colpo.
        /// </summary>
        public void DrawWeapon(Vector3 eye, float yaw, float pitch, float swing, float sway)
        {
            var camera = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            // a riposo la lama è inclinata in avanti e verso sinistra; il colpo la porta da destra a sinistra
            float sweep = swing > 0 ? MathF.Sin(swing * MathF.PI) : 0;
            var local = Quaternion.Concatenate(
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.55f + sweep * 0.9f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.35f - (swing > 0 ? (swing * 1.9f - 0.5f) : 0)));
            var q = Quaternion.Concatenate(local, camera);
            Vector3 C(float x, float y, float z) => eye + Vector3.Transform(new Vector3(x, y, z), camera);
            var hilt = C(-0.3f + sweep * 0.22f, -0.34f + MathF.Sin(sway) * 0.012f, 0.55f);
            var along = Vector3.Transform(Vector3.UnitZ, q);
            var steel = new Color((byte)0xd8, (byte)0xd2, (byte)0xc4, (byte)255);
            Oriented(_cube, hilt + along * 0.3f, q, new(0.04f, 0.012f, 0.52f), steel);
            Oriented(_cube, hilt + along * 0.02f, q, new(0.12f, 0.025f, 0.03f), Theme.Gilt);
            Oriented(_cube, hilt - along * 0.07f, q, new(0.025f, 0.025f, 0.12f), Theme.Basalt);
            Sphere(hilt - along * 0.14f, 0.022f, Theme.Gilt);
        }

        public void DrawShot(Shot shot, double time)
        {
            float k = (float)Math.Clamp((time - shot.Time) / 0.18, 0, 1);
            var dir = Vector3.Normalize(shot.To - shot.From);
            var at = Vector3.Lerp(shot.From, shot.To, k);
            float yaw = MathF.Atan2(dir.X, dir.Z), pitch = MathF.Asin(Math.Clamp(dir.Y, -1, 1));
            var q = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            Oriented(_cube, at, q, new(0.025f, 0.025f, 0.5f), Theme.Gilt);
        }

        /// <summary>Disegna un modello ruotato da un quaternione (raylib vuole asse e angolo).</summary>
        private static void Oriented(Model model, Vector3 center, Quaternion q, Vector3 size, Color color)
        {
            q = Quaternion.Normalize(q);
            if (q.W < 0) q = -q;
            float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
            float s = MathF.Sqrt(MathF.Max(0f, 1f - q.W * q.W));
            var axis = s < 1e-4f ? Vector3.UnitY : new Vector3(q.X, q.Y, q.Z) / s;
            Raylib.DrawModelEx(model, center, axis, angle * 180f / MathF.PI, size, color);
        }

        private bool TouchesOpenSpace(int x, int z)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_map[x + dx, z + dz] is Tile.Floor or Tile.Column or Tile.Altar) return true;
            return false;
        }

        private static bool Near(Vector3 eye, float x, float z) =>
            (eye.X - x) * (eye.X - x) + (eye.Z - z) * (eye.Z - z) < Lighting.VisibleDistance * Lighting.VisibleDistance;

        private void Box(Vector3 center, Vector3 size, Color color) =>
            Raylib.DrawModelEx(_cube, center, Vector3.UnitY, 0, size, color);

        private void Rotated(Vector3 center, Vector3 size, float degrees, Color color) =>
            Raylib.DrawModelEx(_cube, center, Vector3.UnitY, degrees, size, color);

        private void Cylinder(Vector3 bottom, float radius, float height, Color color) =>
            Raylib.DrawModelEx(_cylinder, bottom, Vector3.UnitY, 0, new(radius, height, radius), color);

        private void Cone(Vector3 bottom, float radius, float height, float degrees, Color color) =>
            Raylib.DrawModelEx(_cone, bottom, Vector3.UnitY, degrees, new(radius, height, radius), color);

        private void Sphere(Vector3 center, float radius, Color color) =>
            Raylib.DrawModelEx(_sphere, center, Vector3.UnitY, 0, new(radius), color);

        private Model Load(Mesh mesh)
        {
            var model = Raylib.LoadModelFromMesh(mesh);
            model.Materials[0].Shader = _lighting.Shader;
            return model;
        }

        public void Dispose()
        {
            foreach (var model in new[] { _cube, _cylinder, _sphere, _cone })
            {
                model.Materials[0].Shader = default; // lo shader lo libera Lighting
                Raylib.UnloadModel(model);
            }
        }
    }
}

