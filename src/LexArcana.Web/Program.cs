using System.Runtime.InteropServices.JavaScript;
using LexArcana.Characters;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Visione;

// Il browser chiama Start una volta e poi Frame a ogni fotogramma (requestAnimationFrame): qui non c'è un ciclo.
return 0;

namespace LexArcana.Web
{
    /// <summary>Il ponte tra la pagina (main.js) e il client 3D.</summary>
    public static partial class Bridge
    {
        private static VisioneApp? _app;
        private const string StorageKey = "lexarcana.personaggio";

        [JSImport("globalThis.lexarcana.askText")]
        private static partial string? AskText(string question);

        [JSImport("globalThis.lexarcana.load")]
        private static partial string? Load(string key);

        [JSImport("globalThis.lexarcana.save")]
        private static partial void Save(string key, string value);

        [JSImport("globalThis.lexarcana.touches")]
        [return: JSMarshalAs<JSType.Array<JSType.Number>>]
        private static partial double[] Touches();

        /// <summary>
        /// Si collega al server che ha servito la pagina ed entra in gioco. Il personaggio è quello salvato in
        /// questo browser se ha lo stesso nome, altrimenti uno nuovo con la vocazione scelta.
        /// Restituisce "" se è andata bene, altrimenti il motivo da mostrare.
        /// </summary>
        [JSExport]
        public static async Task<string> Start(string wsUrl, string name, string vocation, string password, bool touch, int width, int height)
        {
            if (!Names.IsValid(name)) return $"Scegli un nome di 1–{Names.MaxLength} lettere o cifre, senza spazi.";
            Character? character = null;
            if (Load(StorageKey) is { Length: > 0 } saved)
            {
                try
                {
                    var c = Character.FromJson(saved);
                    if (c.Player.IsSameName(name) && c.Validate().Count == 0 && (vocation.Length == 0 || c.Vocation.Equals(vocation, StringComparison.OrdinalIgnoreCase))) character = c;
                }
                catch (FormatException) { }
            }
            character ??= Character.Random(name, Random.Shared.Next(), vocation.Length == 0 ? null : vocation);
            Save(StorageKey, character.ToJson());

            GameClient client;
            try
            {
                client = await GameClient.ConnectWebSocketAsync(new Uri(wsUrl), name, password.Length == 0 ? null : password);
            }
            catch (ConnectionRefusedException ex) { return "Il Demiurgo ha rifiutato l'accesso: " + ex.Message; }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException) { return "Impossibile raggiungere il server: " + ex.Message; }

            // raylib legge i font dal file system di WebAssembly: li si scarica dalla pagina e li si scrive lì.
            try
            {
                var page = new UriBuilder(new Uri(wsUrl)) { Scheme = wsUrl.StartsWith("wss:", StringComparison.Ordinal) ? "https" : "http", Path = "/" }.Uri;
                using var http = new HttpClient { BaseAddress = page };
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));
                foreach (var font in new[] { "EBGaramond.ttf", "Cinzel.ttf" })
                    await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", font), await http.GetByteArrayAsync("fonts/" + font));
            }
            catch (HttpRequestException) { /* senza font si gioca lo stesso, con il carattere di riserva */ }

            if (!GameMap.TryParse([$"nome: {client.Welcome.Map.Name}", .. client.Welcome.Map.Rows], out var map, out var error))
                return "Il server ha mandato una mappa non valida: " + error;

            var options = new VisioneOptions("", 0, name, null, null, 0, Character: character)
            {
                Touch = touch,
                AskText = () => AskText("Che cosa dici? (anche /tira d20, /scheda, /sussurra …)"),
                SaveCharacter = c => Save(StorageKey, c.ToJson()),
                TouchPoints = Touches,
            };
            _app = new VisioneApp(client, map, options);
            _app.Start(width, height);
            return "";
        }

        [JSExport]
        public static bool Frame() => _app?.Frame() ?? false;

        [JSExport]
        public static void Resize(int width, int height) => Raylib_cs.Raylib.SetWindowSize(width, height);

        /// <summary>Il personaggio salvato in questo browser, da mostrare nella pagina d'ingresso.</summary>
        [JSExport]
        public static string SavedCharacter()
        {
            try
            {
                return Load(StorageKey) is { Length: > 0 } s && Character.FromJson(s) is var c
                    ? $"{c.Player}|{c.RomanName}|{c.Vocation}|{c.Level}"
                    : "";
            }
            catch (FormatException) { return ""; }
        }
    }
}
