# PatchIT Desktop

App desktop Windows per sfogliare il catalogo delle traduzioni amatoriali di [PatchIT](https://patchit-traduzioni.vercel.app) e scaricarle con un clic.

![Screenshot di PatchIT Desktop](screenshot.png)

## Scarica

1. Vai sulla pagina [Releases](https://github.com/matteogit-dev/patchit-desktop/releases)
2. Scarica `PatchIT.exe` dall'ultima release
3. Avviala: **non serve installare niente**

Requisiti: Windows 10/11 a 64 bit. Nessun runtime .NET richiesto (build self-contained).

> Windows può mostrare il avviso "Windows ha protetto il PC" al primo avvio (l'exe non è firmata digitalmente): clicca **Più informazioni → Esegui comunque**.

## Funzionalità

- **Catalogo completo** con cover, ricerca e filtri per stato (completate, in corso, in pausa, pianificate)
- **Download 1-click** con barra di progresso, nella cartella che preferisci (di default `Download\PatchIT`)
- **Controllo aggiornamenti**: se una patch scaricata è obsoleta rispetto al sito, appare il badge "Aggiornamento disponibile"
- **Istruzioni di installazione** e changelog integrati nella scheda di ogni gioco
- Le patch distribuite su **Steam Workshop** si aprono direttamente nel browser

I dati arrivano da `catalog.json`, generato automaticamente dal sito durante il build.

## Compilare da sorgenti

Le sorgenti complete vivono nella cartella `client/` del repository del sito (privato); questo repository è la **copia pubblica per le release** — va aggiornato a ogni nuova release.

> ⚠️ **`dotnet build` NON basta.** Compila solo `bin/Debug` e lascia `release/PatchIT.exe` com'era. L'exe distribuito si aggiorna **solo** con `dotnet publish`.

```powershell
dotnet publish PatchIt.Client.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o release
```

L'output è un unico `release/PatchIT.exe` (~65 MB) da caricare come asset della release.

### Pubblicare una nuova release

1. Aggiorna `<Version>` in `PatchIt.Client.csproj` (es. `1.1.0`), poi `git commit` e `git push`.
2. Rigenera l'exe con il comando `dotnet publish` qui sopra (sovrascrive `release/PatchIT.exe`).
3. Elimina `release/PatchIT.pdb`: non serve distribuirlo.
4. Crea e carica il tag: `git tag v1.1.0` e `git push origin v1.1.0`.
5. Su GitHub: **Releases → Draft a new release** → tag `v1.1.0` → allega `release/PatchIT.exe` → **Publish**.

Nota: `release/` è in `.gitignore`, quindi l'exe **non** finisce nel repo: va caricato solo come asset della release.

## License

Traduzioni e codice: uso personale e non commerciale. Non ridistribuire le patch senza autorizzazione.
