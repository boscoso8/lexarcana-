using LexArcana.Characters;

namespace LexArcana.Campaigns;

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
