using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using LexArcana.Net;
using LexArcana.Protocol;

namespace LexArcana.Tests;

/// <summary>Sessioni vere su loopback: un server e client collegati via TCP.</summary>
public sealed class SessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Raccoglie i messaggi di un client o del Demiurgo per esaminarli in ordine.</summary>
    private sealed class Inbox
    {
        private readonly Channel<ServerMessage> _messages = Channel.CreateUnbounded<ServerMessage>();
        public void Add(ServerMessage m) => _messages.Writer.TryWrite(m);

        public Task<T> NextAsync<T>() where T : ServerMessage => NextAsync<T>(Wait);

        /// <summary>Il prossimo messaggio di tipo T, saltando gli altri.</summary>
        public async Task<T> NextAsync<T>(TimeSpan within) where T : ServerMessage
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(within);
            await foreach (var m in _messages.Reader.ReadAllAsync(timeout.Token))
                if (m is T match) return match;
            throw new InvalidOperationException();
        }

        /// <summary>True se arriva un messaggio di tipo T entro il tempo dato. Consuma i messaggi letti.</summary>
        public async Task<bool> SeesAsync<T>(TimeSpan within) where T : ServerMessage
        {
            try
            {
                await NextAsync<T>(within);
                return true;
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested) { return false; }
        }
    }

    private static (GameServer Server, Inbox Demiurge) StartServer(string? password = null)
    {
        var inbox = new Inbox();
        var server = new GameServer(IPAddress.Loopback, 0, inbox.Add, password);
        server.Start();
        return (server, inbox);
    }

    private static async Task<(GameClient Client, Inbox Inbox)> JoinAsync(GameServer server, string name, string? password = null)
    {
        var client = await GameClient.ConnectAsync("127.0.0.1", server.Port, name, password, Ct);
        var inbox = new Inbox();
        _ = Task.Run(async () =>
        {
            await foreach (var m in client.ReadAllAsync(Ct)) inbox.Add(m);
        }, Ct);
        return (client, inbox);
    }

    [Fact]
    public async Task Players_join_chat_and_see_public_rolls_made_by_the_server()
    {
        var (server, demiurge) = StartServer();
        await using var _ = server;
        var (marco, marcoInbox) = await JoinAsync(server, "Marco");
        var (livia, liviaInbox) = await JoinAsync(server, "Livia");
        Assert.Equal("Livia", (await marcoInbox.NextAsync<Joined>()).Name);

        marco.Send(new Say("Ave, Livia!"));
        Assert.Equal(new Said("Marco", "Ave, Livia!"), await liviaInbox.NextAsync<Said>());
        Assert.Equal("Ave, Livia!", (await demiurge.NextAsync<Said>()).Text);

        livia.Send(new RollDice("3d6+1"));
        var roll = await marcoInbox.NextAsync<Rolled>();
        Assert.Equal("Livia", roll.From);
        Assert.Equal(3, roll.Dice.Length);
        Assert.Equal(roll.Dice.Sum() + 1, roll.Total);

        await livia.DisposeAsync();
        Assert.Equal("Livia", (await marcoInbox.NextAsync<Left>()).Name);
        await marco.DisposeAsync();
    }

    [Fact]
    public async Task Secret_rolls_and_whispers_reach_only_the_people_involved_and_the_demiurge()
    {
        var (server, demiurge) = StartServer();
        await using var _ = server;
        var (marco, marcoInbox) = await JoinAsync(server, "Marco");
        var (livia, liviaInbox) = await JoinAsync(server, "Livia");
        var (aulo, auloInbox) = await JoinAsync(server, "Aulo");

        marco.Send(new RollDice("d20", Secret: true));
        Assert.True((await marcoInbox.NextAsync<Rolled>()).Secret);
        Assert.Equal("Marco", (await demiurge.NextAsync<Rolled>()).From);

        marco.Send(new Whisper("livia", "Fidati di me."));
        Assert.Equal(new Whispered("Marco", "Livia", "Fidati di me."), await liviaInbox.NextAsync<Whispered>());
        Assert.Equal("Livia", (await marcoInbox.NextAsync<Whispered>()).To);
        Assert.Equal("Fidati di me.", (await demiurge.NextAsync<Whispered>()).Text);

        Assert.False(await auloInbox.SeesAsync<Whispered>(TimeSpan.FromMilliseconds(400)));
        Assert.False(await auloInbox.SeesAsync<Rolled>(TimeSpan.FromMilliseconds(200)));
        Assert.False(await liviaInbox.SeesAsync<Rolled>(TimeSpan.FromMilliseconds(200)));

        await Task.WhenAll(marco.DisposeAsync().AsTask(), livia.DisposeAsync().AsTask(), aulo.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Demiurge_narrates_edits_sheets_rolls_in_secret_and_expels()
    {
        var (server, demiurge) = StartServer();
        await using var _ = server;
        var (marco, marcoInbox) = await JoinAsync(server, "Marco");

        await server.ExecuteAsync("/narra Il tempio è avvolto nella nebbia.");
        Assert.Equal("Il tempio è avvolto nella nebbia.", (await marcoInbox.NextAsync<Narrated>()).Text);

        marco.Send(new SetField("Ferite", "0"));
        Assert.Equal("0", (await marcoInbox.NextAsync<SheetShown>()).Fields["Ferite"]);

        Assert.IsType<SheetShown>(await server.ExecuteAsync("/imposta marco Ferite 2"));
        var pushed = await marcoInbox.NextAsync<SheetShown>();
        Assert.Equal(("Demiurgo", "2"), (pushed.UpdatedBy, pushed.Fields["Ferite"]));

        Assert.Null(await server.ExecuteAsync("/segreto 2d10"));
        Assert.Equal("Demiurgo", (await demiurge.NextAsync<Rolled>()).From);
        Assert.False(await marcoInbox.SeesAsync<Rolled>(TimeSpan.FromMilliseconds(300)));

        Assert.IsType<Refused>(await server.ExecuteAsync("/tira 9d"));
        Assert.IsType<Refused>(await server.ExecuteAsync("/volare"));

        await server.ExecuteAsync("/espelli Marco disturbi la quiete");
        Assert.Equal("disturbi la quiete", (await marcoInbox.NextAsync<Expelled>()).Reason);
        Assert.Equal("Marco", (await demiurge.NextAsync<Left>()).Name);
        await marco.DisposeAsync();
    }

    [Theory]
    [InlineData("Marco", "già in uso")]
    [InlineData("demiurgo", "riservato")]
    [InlineData("due parole", "nome")]
    public async Task Refuses_bad_names(string name, string reason)
    {
        var (server, _) = StartServer();
        await using var _s = server;
        var (marco, _) = await JoinAsync(server, "Marco");
        var ex = await Assert.ThrowsAsync<ConnectionRefusedException>(() => GameClient.ConnectAsync("127.0.0.1", server.Port, name, ct: Ct));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        await marco.DisposeAsync();
    }

    [Fact]
    public async Task Requires_the_password_when_one_is_set()
    {
        var (server, _) = StartServer(password: "ave");
        await using var _s = server;
        var ex = await Assert.ThrowsAsync<ConnectionRefusedException>(() => GameClient.ConnectAsync("127.0.0.1", server.Port, "Livia", "sbagliata", Ct));
        Assert.Contains("Parola d'ordine", ex.Message, StringComparison.Ordinal);
        await using var ok = await GameClient.ConnectAsync("127.0.0.1", server.Port, "Livia", "ave", Ct);
        Assert.Equal("Livia", ok.Name);
    }

    [Fact]
    public async Task Oversized_lines_close_the_connection_without_hurting_the_server()
    {
        var (server, _) = StartServer();
        await using var _s = server;
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port, Ct);
            var stream = raw.GetStream();
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(new string('x', LineConnection.MaxLineBytes * 2)), Ct); }
            catch (IOException) { }
            var reply = new byte[256];
            int read;
            try { read = await stream.ReadAsync(reply, Ct).AsTask().WaitAsync(Wait, Ct); }
            catch (IOException) { read = 0; }
            Assert.True(read == 0 || Encoding.UTF8.GetString(reply, 0, read).Contains("refused", StringComparison.Ordinal));
        }

        await using var marco = await GameClient.ConnectAsync("127.0.0.1", server.Port, "Marco", ct: Ct);
        Assert.Contains("Marco", server.PlayerNames);
    }

    [Fact]
    public async Task Closing_the_server_tells_every_player()
    {
        var (server, _) = StartServer();
        var (marco, marcoInbox) = await JoinAsync(server, "Marco");
        await server.DisposeAsync();
        Assert.Contains("chiuso", (await marcoInbox.NextAsync<Expelled>()).Reason, StringComparison.Ordinal);
        await marco.DisposeAsync();
    }
}
