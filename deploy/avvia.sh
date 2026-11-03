#!/bin/sh
# Avvia il server del tavolo con le impostazioni prese dalle variabili d'ambiente:
#   LEXARCANA_PAROLA            parola d'ordine dei giocatori (vuota: tavolo aperto)
#   LEXARCANA_PAROLA_DEMIURGO   parola d'ordine per condurre da remoto (consigliata: senza, nessuno può condurre online)
#   LEXARCANA_CAMPAGNA          campagna da caricare, es. /dati/il-culto-della-luna-nera/campagna.json
#   LEXARCANA_AVVENTURA         avventura da cui partire (predefinito 1)
#   LEXARCANA_MAPPA             in alternativa alla campagna, un file di mappa
set -eu
set -- server --porta 7777 --porta-web 8080 --web /app/web --senza-colori "$@"
[ -n "${LEXARCANA_PAROLA:-}" ] && set -- "$@" --parola-d-ordine "$LEXARCANA_PAROLA"
[ -n "${LEXARCANA_PAROLA_DEMIURGO:-}" ] && set -- "$@" --parola-demiurgo "$LEXARCANA_PAROLA_DEMIURGO"
if [ -n "${LEXARCANA_CAMPAGNA:-}" ]; then
  set -- "$@" --campagna "$LEXARCANA_CAMPAGNA" --avventura "${LEXARCANA_AVVENTURA:-1}"
elif [ -n "${LEXARCANA_MAPPA:-}" ]; then
  set -- "$@" --mappa "$LEXARCANA_MAPPA"
fi
exec /app/lexarcana "$@"
