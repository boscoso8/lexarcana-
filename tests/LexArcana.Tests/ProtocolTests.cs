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

        var hello = Assert.IsType<Hello>(Wire.DecodeClient(Wire.Encode(new Hello("Marco"))));
        Assert.Equal(("Marco", Wire.ProtocolVersion), (hello.Name, hello.Version));
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
