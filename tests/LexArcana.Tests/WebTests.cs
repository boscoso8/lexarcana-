using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using LexArcana.Net;
using LexArcana.Protocol;

namespace LexArcana.Tests;

/// <summary>La porta web: giocatori dal browser via WebSocket e i file del client web.</summary>
public sealed class WebTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static Task SendAsync(ClientWebSocket ws, ClientMessage message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(message)), WebSocketMessageType.Text, true, Ct);

    [Fact]
    public async Task A_browser_player_joins_over_websocket_and_meets_a_tcp_player()
    {
        var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true);
        server.Start();
        int webPort = server.StartWeb(IPAddress.Loopback, 0, null);
        await using var _s = server;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{webPort}/ws"), Ct);
        await SendAsync(ws, new Hello("Livia"));
        var welcome = Assert.IsType<Welcome>(Wire.DecodeServer(await ReceiveAsync(ws)));
        Assert.Equal("Livia", welcome.Name);

        await using var marco = await GameClient.ConnectAsync("127.0.0.1", server.Port, "Marco", ct: Ct);
        Assert.Contains("Livia", marco.Welcome.Players);

        // Livia dal browser parla, Marco via TCP la sente
        await SendAsync(ws, new Say("Ave dal telefono!"));
        await foreach (var m in marco.ReadAllAsync(Ct))
            if (m is Said said) { Assert.Equal(("Livia", "Ave dal telefono!"), (said.From, said.Text)); break; }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
    }

    [Fact]
    public async Task The_web_port_serves_the_client_files_and_nothing_outside_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "lexarcana-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "_framework"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><title>Lex Arcana</title>");
        File.WriteAllBytes(Path.Combine(root, "_framework", "dotnet.native.wasm"), [0, 0x61, 0x73, 0x6d]);
        File.WriteAllText(Path.Combine(root, ".segreto"), "no");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lexarcana-fuori.txt"), "no");
        try
        {
            var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true);
            server.Start();
            int webPort = server.StartWeb(IPAddress.Loopback, 0, root);
            await using var _s = server;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}/") };

            var index = await http.GetAsync("", Ct);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains("Lex Arcana", await index.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

            var wasm = await http.GetAsync("_framework/dotnet.native.wasm", Ct);
            Assert.Equal("application/wasm", wasm.Content.Headers.ContentType?.MediaType);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(".segreto", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("%2e%2e/lexarcana-fuori.txt", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("manca.js", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("", new StringContent("x"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("ws", Ct)).StatusCode);   // /ws senza upgrade

            // un percorso con ../ scritto a mano, che HttpClient normalizzerebbe
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, webPort, Ct);
            var stream = raw.GetStream();
            await stream.WriteAsync("GET /../lexarcana-fuori.txt HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), Ct);
            var reply = new byte[256];
            int n = await stream.ReadAsync(reply, Ct);
            Assert.StartsWith("HTTP/1.1 404", Encoding.ASCII.GetString(reply, 0, n), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(Path.Combine(Path.GetTempPath(), "lexarcana-fuori.txt"));
        }
    }

    [Theory]
    [InlineData("https://tavolo.esempio.it", "wss://tavolo.esempio.it/ws")]
    [InlineData("http://192.168.1.20:8080", "ws://192.168.1.20:8080/ws")]
    [InlineData("wss://tavolo.esempio.it/ws", "wss://tavolo.esempio.it/ws")]
    [InlineData("192.168.1.20", null)]
    [InlineData("tavolo.esempio.it", null)]
    public void Web_addresses_become_websocket_addresses_and_plain_hosts_stay_tcp(string host, string? expected) =>
        Assert.Equal(expected, GameClient.WebSocketUri(host)?.ToString());

    [Fact]
    public async Task A_desktop_client_can_join_an_online_table_through_its_web_address()
    {
        var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true);
        server.Start();
        int webPort = server.StartWeb(IPAddress.Loopback, 0, null);
        await using var _s = server;
        await using var marco = await GameClient.ConnectToAsync($"http://127.0.0.1:{webPort}", 0, "Marco", ct: Ct);
        Assert.Equal("Marco", marco.Name);
        Assert.Contains("Marco", server.PlayerNames);
    }

    [Fact]
    public async Task The_demiurge_can_lead_an_online_table_remotely()
    {
        var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true, demiurgePassword: "fascio");
        server.Start();
        int webPort = server.StartWeb(IPAddress.Loopback, 0, null);
        await using var _s = server;
        var table = $"http://127.0.0.1:{webPort}";

        await Assert.ThrowsAsync<ConnectionRefusedException>(() => GameClient.ConnectToAsync(table, 0, "Demiurgo", "sbagliata", Ct));
        await using var demiurge = await GameClient.ConnectToAsync(table, 0, "Demiurgo", "fascio", Ct);
        await using var livia = await GameClient.ConnectToAsync(table, 0, "Livia", ct: Ct);
        Assert.DoesNotContain("Demiurgo", server.PlayerNames);   // non è un giocatore

        demiurge.Send(new Command("/narra Le porte si aprono."));
        await foreach (var m in livia.ReadAllAsync(Ct))
            if (m is Narrated n) { Assert.Equal("Le porte si aprono.", n.Text); break; }

        // il Demiurgo remoto sente i sussurri e riceve le risposte ai suoi comandi
        livia.Send(new Whisper("Demiurgo", "Ho un piano."));
        demiurge.Send(new Command("/evoca strige"));      // in un tavolo senza nemici: la lista arriva a tutti
        demiurge.Send(new Command("/fine"));
        bool heardWhisper = false, refusedEnd = false;
        await foreach (var m in demiurge.ReadAllAsync(Ct))
        {
            if (m is Whispered { Text: "Ho un piano." }) heardWhisper = true;
            if (m is Refused r && r.Reason.Contains("console", StringComparison.Ordinal)) refusedEnd = true;
            if (heardWhisper && refusedEnd) break;
        }
        Assert.True(heardWhisper && refusedEnd);
    }

    [Fact]
    public async Task Without_a_demiurge_password_the_name_stays_reserved()
    {
        var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true);
        server.Start();
        await using var _s = server;
        var ex = await Assert.ThrowsAsync<ConnectionRefusedException>(() => GameClient.ConnectAsync("127.0.0.1", server.Port, "Demiurgo", "qualsiasi", Ct));
        Assert.Contains("riservato", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_websocket_messages_close_the_connection()
    {
        var server = new GameServer(IPAddress.Loopback, 0, _ => { }, peaceful: true);
        server.Start();
        int webPort = server.StartWeb(IPAddress.Loopback, 0, null);
        await using var _s = server;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{webPort}/ws"), Ct);
        await ws.SendAsync(new byte[LineConnection.MaxLineBytes * 2], WebSocketMessageType.Text, true, Ct);
        var buffer = new byte[1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, timeout.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (WebSocketException) { }
        Assert.NotEqual(WebSocketState.Open, ws.State);
    }
}
