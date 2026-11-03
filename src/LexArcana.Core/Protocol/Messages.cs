using System.Text.Json.Serialization;

namespace LexArcana.Protocol;

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
public abstract record ServerMessage;

public sealed record Welcome(string Name, string[] Players, int Version = Wire.ProtocolVersion) : ServerMessage;
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
