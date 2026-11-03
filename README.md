# Lex Arcana

Un tavolo da gioco in rete per giocare di ruolo. Il **Demiurgo** apre la sessione come server dal proprio terminale e i **giocatori** si collegano come client da altri computer. Ci sono chat, narrazione, sussurri, tiri di dado fatti dal server (nessuno può barare) e schede dei personaggi condivise.

I giocatori possono collegarsi in due modi:

- **`lexarcana-visione`**: una finestra 3D in prima persona. Cammini nel luogo scelto dal Demiurgo e vedi gli altri personaggi muoversi intorno a te. Chat e comandi restano nella finestra.
- **`lexarcana client`**: solo testo, nel terminale.

![Il tempio di Giano visto dal client 3D](docs/tempio.png)

Richiede **.NET 10** (C# 14).

## Avvio rapido

Il Demiurgo apre la sessione:

```bash
dotnet run --project src/LexArcana -- server --porta 7777 --parola-d-ordine ave
```

Ogni giocatore si collega:

```bash
dotnet run --project src/LexArcana -- client --host 192.168.1.20 --porta 7777 --nome Livia --parola-d-ordine ave
```

Senza `--parola-d-ordine` chiunque raggiunga la porta può entrare. Il server ascolta su tutte le interfacce (`0.0.0.0`); con `--indirizzo 127.0.0.1` accetta solo connessioni dallo stesso computer.

Per entrare in 3D:

```bash
dotnet run --project src/LexArcana.Visione -- --host 192.168.1.20 --porta 7777 --nome Aurelia --parola-d-ordine ave
```

Nella finestra: **W A S D** per muoversi, **Maiusc** per correre, il **mouse** per guardare intorno, **Invio** o **T** per parlare, **/** per un comando (gli stessi del terminale), **Tab** per vedere chi è presente, **Esc** per liberare il mouse, **F1** per l'aiuto. Serve una scheda grafica con OpenGL 3.3.

![Due personaggi nel corridoio d'ingresso](docs/personaggi.png)

### Mappe

Il Demiurgo sceglie il luogo con `--mappa`:

```bash
dotnet run --project src/LexArcana -- server --mappa mappe/foro.txt
```

Una mappa è un file di testo in cui ogni carattere è una casella di 2 metri: `#` muro, `.` pavimento, `C` colonna, `A` altare con fuoco, `S` punto di partenza, spazio per il vuoto. Una prima riga `nome: …` dà il nome al luogo. Senza `--mappa` si gioca nel Tempio di Giano. Il server manda la mappa a ogni giocatore che entra, quindi basta che ce l'abbia il Demiurgo.

### Eseguibile nativo

```bash
dotnet publish src/LexArcana -c Release -r linux-x64
```

Il risultato in `src/LexArcana/bin/Release/net10.0/linux-x64/publish/lexarcana` è compilato con Native AOT: si avvia subito e funziona su computer senza .NET installato. Su Windows usa `-r win-x64`, su macOS `-r osx-arm64`; serve il toolchain C/C++ della piattaforma (vedi la documentazione di Native AOT).

Per distribuire il client 3D senza richiedere .NET:

```bash
dotnet publish src/LexArcana.Visione -c Release -r win-x64 --self-contained
```

## Comandi

| Giocatore | |
| --- | --- |
| `<testo>` | parla a tutta la compagnia |
| `/tira 3d6+2` | tiro pubblico |
| `/segreto d20` | tiro che vedete solo tu e il Demiurgo |
| `/sussurra <nome> <testo>` | messaggio privato (il Demiurgo sente tutto) |
| `/scheda [nome]` | la tua scheda o quella di un compagno |
| `/imposta <campo> [valore]` | scrivi un campo della tua scheda; senza valore lo cancelli |
| `/chi`, `/aiuto`, `/esci` | presenti, aiuto, uscita |

| Demiurgo | |
| --- | --- |
| `<testo>` | parla a tutti |
| `/narra <testo>` | narrazione in evidenza |
| `/tira <dadi>`, `/segreto <dadi>` | tiro pubblico o visibile solo a te |
| `/sussurra <nome> <testo>` | messaggio privato a un giocatore |
| `/scheda <nome>`, `/imposta <nome> <campo> [valore]` | leggi e modifica le schede |
| `/espelli <nome> [motivo]` | allontana un giocatore |
| `/chi`, `/aiuto`, `/fine` | presenti, aiuto, chiusura della sessione |

I dadi usano la notazione standard: `d20`, `3d6+2`, `1d8+1d4-1` (fino a 100 dadi da 2–1000 facce).

## Struttura

- `src/LexArcana.Core`: la logica, senza dipendenze esterne.
  - `Protocol/`: i messaggi. Ogni messaggio è un record C#, inviato come una riga JSON con un campo `type`. La serializzazione è generata a compilazione.
  - `Net/`: `GameServer` (la sessione del Demiurgo), `GameClient`, `LineConnection` (righe TCP con limite di 16 KB e coda di invio per connessione) e l'interpretazione dei comandi.
  - `Game/`: dadi, schede, regole su nomi e testo.
  - `Ui/`: i colori del design system Lex Arcana (tema Night) come ANSI a 24 bit. Rispetta `NO_COLOR` e `--senza-colori`.
  - `Game/GameMap.cs`: le mappe, le collisioni (anche il server le usa per rifiutare chi attraversa i muri) e la linea di vista.
- `src/LexArcana`: l'eseguibile (`server` / `client`).
- `src/LexArcana.Visione`: il client 3D con [Raylib-cs](https://github.com/raylib-cs/raylib-cs).
  - `World.cs`: il luogo e i personaggi.
  - `Lighting.cs`: lo shader, con la lanterna del personaggio, i fuochi degli altari, la luna e la nebbia.
  - `Hud.cs`: chat e pannelli.

  Colori e font vengono dal design system Lex Arcana. I font, EB Garamond e Cinzel, sono distribuiti con licenza SIL Open Font License (`Assets/Fonts/OFL-*.txt`).
- `tests/LexArcana.Tests`: test xUnit v3, comprese sessioni TCP reali su loopback.

```bash
dotnet test
```

Per controllare il rendering senza schermo (per esempio in CI), il client 3D può salvare un'immagine e uscire:

```bash
xvfb-run -s "-screen 0 1280x720x24" lexarcana-visione --nome Prova --cattura prova.png --dopo 3
```

## Sicurezza

La connessione non è cifrata: la parola d'ordine e i messaggi viaggiano in chiaro. Usala in una rete fidata, o attraverso una VPN o un tunnel SSH. Il server tira tutti i dadi, controlla ogni spostamento (niente muri attraversati né teletrasporti), toglie i caratteri di controllo da ogni testo (niente sequenze ANSI verso i terminali degli altri) e chiude le connessioni che inviano righe troppo lunghe o non leggono i messaggi.
