# Il tavolo online di Lex Arcana: il server del Demiurgo con il client web (browser, iPhone).
# Si costruisce dalla cartella del repository:
#   docker build -f deploy/Dockerfile -t lexarcana .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN dotnet workload install wasm-tools
WORKDIR /src
COPY . .
RUN dotnet publish src/LexArcana -c Release -r linux-x64 --self-contained \
        -p:PublishSingleFile=true -p:PublishAot=false -p:PublishTrimmed=true -p:DebugType=none -o /out \
 && dotnet publish src/LexArcana.Web -c Release -o /web \
 && cp -r /web/wwwroot /out/web \
 && cp -r mappe /out/mappe \
 && cp deploy/avvia.sh /out/avvia.sh && chmod +x /out/avvia.sh /out/lexarcana

# Il risultato non ha bisogno di .NET: il runtime 10.0.14 è dentro l'eseguibile.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS tavolo
WORKDIR /app
COPY --from=build /out .
# /dati: le campagne (campagna.json e mappe) da caricare con LEXARCANA_CAMPAGNA
USER root
RUN mkdir -p /dati && chown app:app /dati
USER app
VOLUME /dati
# 8080: pagina web e WebSocket (dietro un proxy HTTPS); 7777: client da terminale e 3D via TCP, facoltativo
EXPOSE 8080 7777
ENTRYPOINT ["/app/avvia.sh"]
