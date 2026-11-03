# Il tavolo online

Questa cartella mette Lex Arcana su un server raggiungibile da internet. Si gioca all'indirizzo `https://tavolo.esempio.it`, da qualunque client:

| Chi | Come entra |
| --- | --- |
| Giocatori su iPhone, Android o computer | il browser, all'indirizzo del tavolo |
| Giocatori con il client 3D | `lexarcana-visione --host https://tavolo.esempio.it --personaggio aurelia.json` |
| Giocatori da terminale | `lexarcana client --host https://tavolo.esempio.it --nome Marco` |
| Il Demiurgo, da casa sua | `lexarcana client --host https://tavolo.esempio.it --nome Demiurgo --parola-d-ordine <parola del Demiurgo>` |

Il Demiurgo remoto vede tutto quello che vedrebbe la console del server, cioè sussurri, tiri segreti e promemoria delle avventure. Può dare tutti i comandi: `/narra`, `/avventura 2`, `/evoca lupo 3`, `/cura` e gli altri. Solo `/fine` resta riservato alla console del server.

## Cosa serve

- Un server con Docker e Docker Compose. Va bene una piccola macchina virtuale con 1 GB di RAM.
- Un nome di dominio puntato all'indirizzo IP del server, con un record A o AAAA.
- Le porte 80 e 443 aperte. Caddy le usa per ottenere e rinnovare da solo il certificato HTTPS.

## Messa in funzione

Dalla cartella `deploy` del repository:

```bash
cp .env.esempio .env
```

Poi apri `.env` e compila `DOMINIO` e, soprattutto, `LEXARCANA_PAROLA_DEMIURGO`. Infine avvia:

```bash
docker compose up -d --build
```

La prima volta la compilazione richiede qualche minuto, perché scarica l'SDK .NET 10 e compila il client web in WebAssembly. Poi apri `https://<il tuo dominio>`.

## Campagne

Le campagne si mettono in `deploy/dati`, che nel container diventa `/dati`:

```bash
docker compose run --rm --entrypoint /app/lexarcana lexarcana campagna --seme 42 --cartella /dati/luna-nera
```

Poi, in `.env`:

```
LEXARCANA_CAMPAGNA=/dati/luna-nera/campagna.json
```

E riavvia:

```bash
docker compose up -d
```

## Condurre dalla console del server

In alternativa al Demiurgo remoto:

```bash
docker attach deploy-lexarcana-1
```

Per staccarsi senza fermare il tavolo: `Ctrl+P` e poi `Ctrl+Q`.

## Sicurezza

- Tutto passa da HTTPS e WebSocket cifrati (`wss://`): parola d'ordine, chat e tiri non viaggiano in chiaro.
- La porta 7777 (TCP in chiaro) non è pubblicata da `docker-compose.yml`. Online i client si collegano con l'indirizzo `https://…`.
- Senza `LEXARCANA_PAROLA_DEMIURGO` nessuno può condurre da remoto. Con `LEXARCANA_PAROLA` vuota chiunque conosca l'indirizzo può entrare come giocatore.
