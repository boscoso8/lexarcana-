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
