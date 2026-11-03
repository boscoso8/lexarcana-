using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Protocol;

namespace LexArcana.Tests;

public class ProtocolTests
{
    [Fact]
    public void Messages_round_trip_as_single_lines_with_a_type_field()
    {
        var json = Wire.Encode(new Rolled("Livia", "2d6", [3, 4], 0, 7));
        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"type\":\"rolled\"", json, StringComparison.Ordinal);
        var back = Assert.IsType<Rolled>(Wire.DecodeServer(json));
        Assert.Equal(("Livia", 7), (back.From, back.Total));
        Assert.Equal([3, 4], back.Dice);

        var welcome = new Welcome("Livia", ["Demiurgo", "Livia"], new MapData("Sala", ["###", "#S#", "###"]), [new Position("Livia", 3, 3, 0.5f)]);
        var back2 = Assert.IsType<Welcome>(Wire.DecodeServer(Wire.Encode(welcome)));
        Assert.Equal(welcome.Map.Rows, back2.Map.Rows);
        Assert.Equal(welcome.Positions, back2.Positions);
        Assert.Equal(new Move(1.5f, 2f, 3f), Wire.DecodeClient(Wire.Encode(new Move(1.5f, 2f, 3f))));

        var hello = Assert.IsType<Hello>(Wire.DecodeClient(Wire.Encode(new Hello("Marco"))));
        Assert.Equal(("Marco", Wire.ProtocolVersion), (hello.Name, hello.Version));
    }

    public static TheoryData<ServerMessage> EveryServerMessage => new((ServerMessage[])[
        new Welcome("Livia", ["Demiurgo"], new MapData("Sala", ["###", "#S#", "###"]), [new Position("Livia", 1, 2, 3)]),
        new Joined("Livia"), new Left("Livia"), new Said("Livia", "Ave"), new Narrated("Nebbia"),
        new Rolled("Livia", "d20", [7], 0, 7), new Rolled("Livia", "d20", [7], 0, 7, Secret: true),
        new Whispered("Livia", "Marco", "psst"), new SheetShown("Livia", new() { ["Ferite"] = "2" }), new SheetShown("Livia", [], "Demiurgo"),
        new PlayerList(["Demiurgo"]), new Refused("no"), new Expelled(), new Expelled("basta"), new Moved("Livia", 1, 2, 3),
        new MapChanged(new MapData("Sala", ["###"]), []), new MapChanged(new MapData("Sala", ["###"]), [], "1. Titolo"),
        new EnemyList([new EnemyInfo(1, "lupo", "Lupo nero", 2, 9, 9, 1, 2, 3, "Normal")]), new EnemyMoves([new EnemyPose(1, 1, 2, 3)]),
        new Struck("Livia", "Lupo nero (liv. 2)", 15, 20, 12, true, false, 6, 3, 9, false, TargetEnemyId: 1),   // colpisce un giocatore: niente AttackerEnemyId
        new Struck("Lupo nero (liv. 2)", "Livia", 2, 5, 14, false, false, 0, 14, 14, false, AttackerEnemyId: 1),
        new Vitals("Livia", 10, 14, 2, 120, 300, false)]);

    /// <summary>
    /// Ogni messaggio del server sopravvive al viaggio in JSON, anche con i campi facoltativi a null.
    /// I campi null non vengono scritti, quindi il decodificatore deve accettarli assenti.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryServerMessage))]
    public void Every_server_message_round_trips(ServerMessage message)
    {
        var back = Wire.DecodeServer(Wire.Encode(message));
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Wire.Encode(message), Wire.Encode(back));
    }

    public static TheoryData<ClientMessage> EveryClientMessage => new((ClientMessage[])[
        new Hello("Livia"), new Hello("Livia", "ave"), new Say("Ave"), new RollDice("d20"), new RollDice("d20", true),
        new Whisper("Marco", "psst"), new SetField("Ferite", "2"), new ShowSheet(), new ShowSheet("Marco"), new ListPlayers(), new Leave(),
        new Move(1, 2, 3), new SetSheet(new() { ["Ferite"] = "2" }), new Embody(LexArcana.Characters.Character.Random("Livia", 1)),
        new Attack(3), new Attack(3, Ranged: true)]);

    [Theory]
    [MemberData(nameof(EveryClientMessage))]
    public void Every_client_message_round_trips(ClientMessage message)
    {
        var back = Wire.DecodeClient(Wire.Encode(message));
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Wire.Encode(message), Wire.Encode(back));
    }

    [Theory]
    [InlineData("non json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"volare\"}")]
    [InlineData("{\"type\":\"say\"}")]
    [InlineData("{\"type\":\"say\",\"text\":null}")]
    public void Invalid_lines_throw_protocol_exception(string line) =>
        Assert.Throws<ProtocolException>(() => Wire.DecodeClient(line));

    [Fact]
    public void Player_commands_translate_to_messages()
    {
        Assert.Equal(new PlayerInput.Send(new RollDice("d20+2", Secret: true)), PlayerCommands.Parse("/segreto d20+2"));
        Assert.Equal(new PlayerInput.Send(new Whisper("Livia", "ci vediamo al tempio")), PlayerCommands.Parse("/sussurra Livia ci vediamo al tempio"));
        Assert.Equal(new PlayerInput.Send(new SetField("Ferite", "2")), PlayerCommands.Parse("/imposta Ferite 2"));
        Assert.Equal(new PlayerInput.Send(new SetField("Ferite", "")), PlayerCommands.Parse("/imposta Ferite"));
        Assert.Equal(new PlayerInput.Send(new Say("Ave!")), PlayerCommands.Parse("Ave!"));
        Assert.Equal(new PlayerInput.Send(new ShowSheet()), PlayerCommands.Parse("/scheda"));
        Assert.IsType<PlayerInput.Quit>(PlayerCommands.Parse("/ESCI"));
        Assert.IsType<PlayerInput.Empty>(PlayerCommands.Parse("   "));
        Assert.IsType<PlayerInput.Invalid>(PlayerCommands.Parse("/tira"));
        Assert.IsType<PlayerInput.Invalid>(PlayerCommands.Parse("/sussurra Livia"));
        Assert.IsType<PlayerInput.Invalid>(PlayerCommands.Parse("/volare"));
    }

    [Fact]
    public void Sheet_keeps_order_updates_case_insensitively_and_removes_empty_values()
    {
        var sheet = new CharacterSheet("Marco");
        Assert.True(sheet.TrySet("Ferite", "0", out _));
        Assert.True(sheet.TrySet("Coraggio", "3", out _));
        Assert.True(sheet.TrySet("ferite", "2", out _));
        Assert.Equal(["Ferite", "Coraggio"], sheet.Snapshot().Keys);
        Assert.Equal("2", sheet.Snapshot()["Ferite"]);
        Assert.True(sheet.TrySet("Coraggio", "", out _));
        Assert.Single(sheet.Snapshot());
        Assert.False(sheet.TrySet("", "x", out _));
        Assert.False(sheet.TrySet("Note", new string('x', CharacterSheet.MaxValueLength + 1), out _));
    }

    [Theory]
    [InlineData("Marco", true)]
    [InlineData("Livia_2", true)]
    [InlineData("", false)]
    [InlineData("due parole", false)]
    [InlineData("nome-davvero-troppo-lungo-per-noi", false)]
    public void Names_are_validated(string name, bool valid) => Assert.Equal(valid, Names.IsValid(name));

    [Fact]
    public void Text_sanitizing_strips_control_characters()
    {
        Assert.Equal("[31mrosso", "\e[31mrosso".Sanitized);
        Assert.Null("   ".AsMessage);
        Assert.Null(new string('a', Text.MaxMessageLength + 1).AsMessage);
        Assert.Equal("Ave", "  Ave \n".AsMessage);
    }
}
