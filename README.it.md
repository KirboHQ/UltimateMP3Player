<p align="center">
  <img src="assets/logo.png" width="128" alt="Logo di Ultimate MP3 Player">
</p>

<h1 align="center">Ultimate MP3 Player</h1>

<p align="center">
  <b>La tua musica, offline e senza pubblicità.</b><br>
  Incolla un link da YouTube Music, Spotify, Apple Music, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp o
  centinaia di altri siti: i brani finiscono nella tua libreria con copertina e tag, pronti da ascoltare.
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest"><img alt="Ultima versione" src="https://img.shields.io/github/v/release/KirboHQ/UltimateMP3Player?style=flat-square&color=7C5CFF&label=versione"></a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases"><img alt="Download" src="https://img.shields.io/github/downloads/KirboHQ/UltimateMP3Player/total?style=flat-square&color=FF4FA3&label=download"></a>
  <img alt="Windows 10 e 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows&logoColor=white">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-x64%20%7C%20arm64-FCC624?style=flat-square&logo=linux&logoColor=black">
  <img alt="macOS 12 o successivo" src="https://img.shields.io/badge/macOS-12%2B-000000?style=flat-square&logo=apple&logoColor=white">
  <img alt="Android 8 o successivo" src="https://img.shields.io/badge/Android-8%2B-3DDC84?style=flat-square&logo=android&logoColor=white">
  <img alt="iOS 15 o successivo, anteprima" src="https://img.shields.io/badge/iOS-15%2B%20anteprima-000000?style=flat-square&logo=apple&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <a href="LICENSE"><img alt="Licenza GPL-3.0" src="https://img.shields.io/badge/licenza-GPL--3.0-6E7781?style=flat-square"></a>
</p>

<p align="center">
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Scarica per Windows" src="https://img.shields.io/badge/Scarica_per_Windows-7C5CFF?style=for-the-badge&logo=windows&logoColor=white" height="42">
  </a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Scarica per Linux" src="https://img.shields.io/badge/Linux-7C5CFF?style=for-the-badge&logo=linux&logoColor=white" height="42">
  </a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Scarica per macOS" src="https://img.shields.io/badge/macOS-7C5CFF?style=for-the-badge&logo=apple&logoColor=white" height="42">
  </a>
  <a href="https://github.com/KirboHQ/UltimateMP3Player/releases/latest">
    <img alt="Scarica per Android" src="https://img.shields.io/badge/Android-7C5CFF?style=for-the-badge&logo=android&logoColor=white" height="42">
  </a>
</p>

<p align="center"><a href="README.md">English</a> · <b>Italiano</b></p>

---

## ✨ In breve

<picture>
  <source media="(max-width: 640px) and (prefers-color-scheme: dark)" srcset="assets/readme/highlights-it-dark-mobile.svg">
  <source media="(max-width: 640px)" srcset="assets/readme/highlights-it-light-mobile.svg">
  <source media="(prefers-color-scheme: dark)" srcset="assets/readme/highlights-it-dark.svg">
  <img src="assets/readme/highlights-it-light.svg" width="100%" alt="In breve: incolli un link, hai la musica · brani nel cloud · un lettore come Spotify · testi sincronizzati · una coda che si riempie da sola · modalità DJ · playlist, preferiti e tag · su tutti i tuoi dispositivi · ascolta insieme">
</picture>

## 🆕 Novità della 3.7.2

- Trascinare un brano per spostarlo (Android, iPhone, e Linux/macOS sugli schermi ad alta risoluzione): la copia che
  segue il dito torna grande quanto la riga, non più un suo angolo ingrandito.
- iPhone e Android: i primi menu aperti scattano meno (un menu viene preparato, senza vederlo, poco dopo l'avvio).

<details>
<summary>3.7.1</summary>

- **Scegli dove va il suono** (Impostazioni › Riproduzione › *Uscita audio*): casse, cuffie, un DAC USB, un monitor in
  HDMI… invece del dispositivo predefinito del sistema. Se lo scolleghi la musica passa a quello predefinito, e torna
  quando lo ricolleghi. Windows, Linux, macOS e Android.
- **iPhone e iPad (anteprima)**: l'app di Android su iOS 15 o successivo, installata gratis con SideStore (niente Mac,
  niente jailbreak, vedi [Installazione](#-installazione)): libreria, playlist, tag, testi, equalizzatore, dissolvenza,
  statistiche, il brano nella schermata di blocco e i tasti delle cuffie; brani e pacchetti `.ump` arrivano dall'app
  File. **Anche i download**, come su Android: link, ricerca online, brani nel cloud, brani simili e brani dell'artista,
  accesso ai siti. iOS non lascia avviare programmi alle app, quindi yt-dlp gira dentro l'app (con Python) e gli MP3 e
  gli M4A li fa iOS stesso: su iPhone si scarica solo l'audio, niente video. Un link `ump://add?url=<link>` (da un
  Comando rapido nel menu Condividi, per esempio) apre l'app su quel link.
- Un menu a tendina aperto non resta più indietro, staccato dalla sua casella, quando la rotella fa scorrere la pagina
  sotto di lui.

</details>

<details>
<summary>3.7</summary>

- **La qualità che vuoi, brano per brano**: nella scheda del link scegli l'audio (MP3 a 320, 256, 192, 128 kbps o il
  file originale) e la risoluzione del video; il brano si ricorda la scelta, anche nel cloud quando lo ascolti dal suo
  link. Tasto destro → **Scarica di nuovo / qualità…** riscarica un brano salvato in un'altra qualità, al posto dei suoi
  file (copertina, testo, statistiche e playlist restano).
- **MP3 per l'auto**: *Esporta* → **In una cartella (MP3)** mette le playlist, i tag o tutta la libreria scelti in una
  cartella (una chiavetta, una scheda SD) come normali file MP3 chiamati "Artista - Titolo", con titolo, artista, album e
  copertina nei tag. I brani nel cloud si scaricano strada facendo. Su tutte le piattaforme, Android compreso.
- **Riproduci brani dell'artista**: accanto a *Riproduci brani simili*, i brani più ascoltati dell'artista, trovati online.
- **Passaggio a tempo di BPM** (Impostazioni › Riproduzione): negli ultimi secondi il brano rallenta o accelera fino ai
  BPM di quello dopo, senza cambiare tono, così entrano a tempo (i BPM mancanti si rilevano da soli).
- **Ascolta insieme**: la coda della stanza si trascina come i *Successivi*; l'icona della libreria e la freccia del
  download funzionano come ovunque; i brani ascoltati in una stanza contano nelle tue statistiche; i brani trovati con la
  ricerca online vanno dritti nella stanza; i link che non si possono scaricare (DRM) si prendono da YouTube da soli,
  nelle stanze e anche nella coda automatica.
- **Trascina per riordinare ovunque**: i siti della ricerca online nelle impostazioni e i *Successivi* su Android, con
  la linea e il rettangolo delle playlist (su Android tenere premuto la maniglia non fa più scorrere via la lista).
- I risultati della ricerca online aprono subito la loro scheda, senza rileggere il link; i brani aggiunti alla libreria
  senza salvarli lasciano la scheda sullo schermo, pronti da ascoltare.
- Statistiche: un filtro *Tutti i brani*. I download contemporanei valgono anche per la coda automatica e per le stanze.
  Passando al DJ il brano va in pausa. Ctrl+frecce nei campi di testo spostano il cursore invece di cambiare brano.
  L'icona del testo è la stessa su Android. Testi: niente più il brano di qualcun altro con lo stesso titolo. Si vede
  l'onda di un brano ascoltato per la prima volta in una stanza. La pagina del DJ si ridisegna meno spesso. La voce DJ
  ora è sotto Ascolta insieme.
- I file della release per Windows hanno nomi come gli altri: `UltimateMP3Player-windows-x64-setup.exe` e
  `UltimateMP3Player-windows-x64.exe`.

</details>

<details>
<summary>3.6</summary>

- **Brani nel cloud** ☁: un brano non ha più bisogno del suo audio sul dispositivo. Nel cloud tiene copertina, testo,
  statistiche e il suo posto nelle playlist e nei Preferiti, e si ascolta dal suo link; quelli che hai ascoltato restano
  pronti per la prossima volta. I brani salvati si ascoltano offline come sempre e una piccola nuvola segna gli altri.
  Il filtro accanto a *Tag* (3.6.2), nella libreria e in ogni playlist, mostra *Tutti i brani*, solo quelli *Sul
  dispositivo* o *Solo nel cloud*; *Riproduci* e *Casuale* suonano quelli che mostra, comodo offline.
- **Aggiungi ora, salva dopo**: decide l'interruttore *Salva l'audio sul dispositivo* nella card del link. Acceso, il
  solito download; spento, brani e playlist intere finiscono nella libreria (e nella playlist) in pochi secondi, nel
  cloud. L'app si ricorda la tua scelta, anche per i pacchetti `.ump` con i soli link. *Salva sul dispositivo* un brano,
  una selezione o una playlist, oppure *Salva tutti quelli nel cloud*, quando li vuoi offline.
- **Pulsanti chiari** (3.6.1): il **+** aggiunge a una playlist, l'**icona della libreria** mette nella libreria (nel
  cloud) un brano consigliato o ascoltato da un link, la **freccia** lo salva sul dispositivo. Il cuore tiene solo il
  brano, non lo scarica mai.
- **Un solo *Elimina…* per tutto**: *Solo il video*, *Solo il testo*, *Dal dispositivo* (resta nella libreria, nel
  cloud) o *Completamente*, con lo spazio che liberi, per un brano o per cento. *Libera spazio…* sulle playlist.
- **Link che si aggiustano da soli**: quando il link di un brano nel cloud non va più (rimosso, DMCA, reso privato)
  l'app trova lo stesso brano su YouTube Music, lo fa partire e tiene il nuovo link. Un link che non si riesce proprio a
  leggere (SoundCloud Go+, rimosso) propone *Riprova con YouTube*, e i download non riusciti *Riprova su YouTube*.
- **Cerca il video…**: un brano salvato da SoundCloud, Spotify e simili prende il suo video da YouTube. Lo scegli tra i
  risultati: il più adatto è già selezionato, con un avviso se la durata è diversa, perché il video va a tempo con
  l'audio. I brani di YouTube lo prendono direttamente dal loro link.
- **Selezione multipla ovunque**: in ogni lista di brani (anche ricerca e Home), nei *Successivi*, nei download e nelle
  playlist, su tutte le piattaforme. Su Android la pressione prolungata apre il menu con *Seleziona* per primo, come
  nella galleria.
- I brani nel cloud funzionano anche nella modalità DJ (si scaricano quando li carichi su una traccia), e la memoria
  temporanea ora può tenere fino a 200 brani.

</details>

<details>
<summary>3.5</summary>

**3.5.1 – 3.5.2**
- Android: niente più chiusure durante i download lunghi, scorrimento alla frequenza piena dello schermo, circa un terzo
  del processore mentre è a schermo, avvio più veloce, ricerca nelle impostazioni, *Accessi ai siti* dentro l'app (per
  playlist private e contenuti con limite d'età), niente scatti dell'audio a schermo spento
- Linux: molto più fluida (soprattutto nelle macchine virtuali e senza accelerazione grafica), testo più pulito, niente
  più chiusura sui risultati della ricerca online

**3.5.0**
- **Android**: la stessa app su telefoni e tablet (Android 8 o successivo), con lo stesso aspetto e le stesse funzioni,
  tranne la modalità DJ e Ascolta insieme. Una barra in basso con Home, Cerca, Libreria, Download e Impostazioni; il
  brano in riproduzione subito sopra, che si apre a tutto schermo con la copertina (che si inclina sotto il dito), il
  video, i testi sincronizzati e la coda. In orizzontale o su un tablet le schede passano in una colonna a sinistra (come
  la barra laterale del computer) e la pagina del brano mette la copertina accanto al titolo e ai pulsanti. Lo schermo va
  alla sua frequenza massima (90, 120 Hz e oltre).
- **Condividi per scaricare**: in YouTube, Spotify, SoundCloud e quasi ogni altra app premi *Condividi → Scarica con
  Ultimate MP3 Player*: l'app si apre sulla pagina dei download con il link già inserito.
- **Ascolta senza scaricare** (su tutte le piattaforme): accanto a *Scarica*, un link si può anche solo ascoltare. I suoi
  brani arrivano nella memoria temporanea poco prima del loro turno, come i consigliati, e restano fuori dalla libreria;
  per tenerne uno c'è *Salva nella libreria* (su Android anche dalla notifica). In una stanza di *Ascolta insieme* lo
  stesso pulsante è *Aggiungi alla stanza*: i brani vanno nella coda della stanza senza essere scaricati nella libreria.
- **Schermata di blocco e notifica**: copertina, precedente / pausa / successivo e il cuore dei Preferiti (e *Salva
  nella libreria* per un brano che stai solo ascoltando), anche dalle cuffie Bluetooth e in auto. Musica e download vanno
  avanti anche a schermo spento.
- **Più leggera per il processore e la batteria** (su tutte le piattaforme): i download in attesa o finiti non tengono
  più un'animazione accesa dietro le quinte (una lista lunga poteva tenere occupato un core del processore), e mentre
  l'app non è a schermo (in background, ridotta a icona, la pagina del brano chiusa) titoli che scorrono, rotelline di
  caricamento, testi e video si fermano.
- Linux e macOS: cambiare lingua dalle impostazioni funziona di nuovo.
- La pressione prolungata è il tasto destro; ci sono i profili (comodi sul tablet di famiglia), le statistiche, i tag,
  i pacchetti `.ump` e gli aggiornamenti automatici dalle release di GitHub.

</details>

<details>
<summary>3.4.0</summary>

- **Linux e macOS**: la stessa app, con lo stesso aspetto e le stesse funzioni, su Linux (x64 e arm64) e macOS (Intel
  e Apple Silicon). Con i controlli multimediali del sistema, l'icona vicino all'orologio, i pacchetti `.ump` e gli
  aggiornamenti automatici.
- **Statistiche**: un ascolto conta quando hai sentito il 75% del brano (a velocità 2× basta metà del tempo), il tempo
  di ascolto conta dal primo secondo, e si aggiornano da soli mentre ascolti. *Seleziona i mai ascoltati* funziona di
  nuovo con qualsiasi ordine e filtro.
- **Controlli multimediali di Windows**: l'app mostra il suo nome e la sua icona invece di *App sconosciuta*
- Un brano consigliato salvato e poi eliminato mentre suona non salta più: finisce dalla memoria
- La ricerca delle impostazioni resta sempre in alto
- Finestre di dialogo (come i termini d'uso): i pulsanti non sfarfallano più sotto il mouse

</details>

## 📥 Installazione

**Windows**

1. Scarica **`UltimateMP3Player-windows-x64-setup.exe`** dall'[ultima release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Avvialo: niente diritti di amministratore e nient'altro da installare. Ti chiede la lingua (italiano o inglese,
   si cambia poi dalle impostazioni).
3. Fatto: l'app si aggiorna da sola dalle release.

> [!NOTE]
> Windows SmartScreen può dire che l'app non è riconosciuta, perché non è firmata con un certificato a pagamento:
> clicca **Ulteriori informazioni → Esegui comunque**.

**Linux** (x64 o arm64, qualsiasi distribuzione recente con un desktop)

1. Scarica **`UltimateMP3Player-linux-x64.tar.gz`** (o `-arm64`) dall'[ultima release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Estrailo in una tua cartella (non in una di sistema come `/opt`: l'app si aggiorna lì dentro), per esempio:
   ```sh
   mkdir -p ~/.local/opt
   tar -xzf UltimateMP3Player-linux-x64.tar.gz -C ~/.local/opt
   ```
3. Avviala una volta con `~/.local/opt/UltimateMP3Player/UltimateMP3Player`: da lì in poi è nel menu delle
   applicazioni e i pacchetti `.ump` si aprono con lei. Al primo avvio scarica da sola yt-dlp, ffmpeg e gli altri
   motori.

> [!NOTE]
> Su GNOME "puro" l'icona vicino all'orologio richiede l'estensione *AppIndicator* (Ubuntu ce l'ha già); senza,
> chiudere la finestra chiude l'app. Se l'app non parte e nomina ICU, installa il pacchetto `libicu` della tua
> distribuzione.

**macOS** (12 Monterey o successivo)

1. Scarica **`UltimateMP3Player-macos-arm64.zip`** per Apple Silicon (M1 e successivi) o `-x64` per i Mac Intel
   dall'[ultima release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest).
2. Apri lo zip e trascina **Ultimate MP3 Player** in *Applicazioni*.
3. La prima volta macOS dice che non può verificare l'app, perché non è firmata con un certificato Apple a pagamento:
   apri **Impostazioni di Sistema → Privacy e sicurezza** e clicca **Apri comunque** (sulle versioni più vecchie: tasto
   destro sull'app → **Apri**).

> [!NOTE]
> Se macOS dice che l'app "è danneggiata", esegui una volta nel Terminale:
> `xattr -dr com.apple.quarantine "/Applications/Ultimate MP3 Player.app"`

**Android** (8 o successivo; non è sul Play Store)

1. Dal telefono apri l'[ultima release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest) e scarica
   **`UltimateMP3Player-android-arm64-v8a.apk`** (quasi tutti i telefoni degli ultimi anni; `-armeabi-v7a` è per i
   vecchi telefoni a 32 bit, `-x86_64` per emulatori e Chromebook).
2. Apri il file scaricato. La prima volta Android chiede di consentire l'installazione di app dal browser (o dal
   gestore file): consentila, torna indietro e premi **Installa**.
3. Fatto. Al primo avvio l'app prepara i suoi motori (pochi secondi), poi si aggiorna da sola: le nuove versioni si
   scaricano con il Wi-Fi e Android chiede prima di installarle.

> [!NOTE]
> Play Protect può dire che l'app è sconosciuta, perché non arriva dal Play Store: premi **Altri dettagli → Installa
> comunque**. La musica resta nella cartella dell'app (*Android › data*, visibile dal computer); se disinstalli l'app,
> Android chiede se tenerla. Per spostare la musica su un altro telefono o computer usa un pacchetto `.ump`.

**iPhone e iPad** (iOS 15 o successivo; un'anteprima; non è sull'App Store)

1. Installa **SideStore** dal tuo PC Windows (o Mac) seguendo la guida ufficiale su [sidestore.io](https://sidestore.io):
   installa app firmate con il tuo Apple ID gratuito, senza Mac e senza jailbreak. Poi attiva la **Modalità
   sviluppatore** (Impostazioni › Privacy e sicurezza, in fondo; l'iPhone si riavvia).
2. In SideStore apri **Sources**, premi **+** e aggiungi
   `https://github.com/KirboHQ/UltimateMP3Player/releases/latest/download/sidestore-source.json`, poi installa
   **Ultimate MP3 Player** da lì. (Oppure scarica `UltimateMP3Player-ios.ipa` dall'[ultima
   release](https://github.com/KirboHQ/UltimateMP3Player/releases/latest) e aprilo con SideStore.)
3. Le nuove versioni compaiono in SideStore (**My Apps › Update**); l'app te lo dice in Impostazioni › Aggiornamenti
   quando ce n'è una.

> [!NOTE]
> Con un Apple ID gratuito un'app resta firmata 7 giorni e SideStore la rinnova da solo (aprilo ogni tanto), e possono
> essere attive al massimo 3 app installate così, SideStore compreso. I tuoi brani sono nell'app File (*Sul mio iPhone ›
> Ultimate MP3 Player › Music*): aggiungine altri da Impostazioni › Libreria. Disinstallando l'app vengono cancellati:
> tieni un pacchetto `.ump`. I download vogliono l'app aperta: in background iOS lascia a un'app solo mezzo minuto
> circa per finirli. Per mandare un link da un'altra app, crea un Comando rapido che prende l'URL condiviso e apre
> `ump://add?url=` seguito da quello.

## 🎵 Funzioni

**Download**
- Link da YouTube Music, Spotify, Apple Music, SoundCloud, YouTube, TikTok, Instagram, Deezer, Bandcamp e centinaia
  di altri siti, grazie a yt-dlp e gallery-dl
- **Ricerca online** dentro l'app: YouTube Music, YouTube, SoundCloud e Deezer accanto ai tuoi brani; un clic apre il
  download
- Salvi l'audio sul dispositivo, oppure aggiungi solo i brani alla libreria nel cloud e li salvi dopo
- MP3 a 320, 256, 192 o 128 kbps o formato originale, video da 360p fino al 4K, scelti per ogni brano e ricordati;
  più download insieme
- *Scarica di nuovo / qualità…*: un brano salvato riscaricato in un'altra qualità, al posto dei suoi file
- Riconosce i doppioni (stessa fonte o stesso brano) e gestisce da solo i limiti dei siti: i download si mettono in
  pausa e riprendono più piano
- Un link che non si riesce a leggere, e i download non riusciti, si possono riprovare su YouTube Music
- Cookie del browser (facoltativi) per playlist private e contenuti con limite d'età (su Android e iPhone accedi ai
  siti direttamente dall'app)
- La musica che hai già: trascina file o cartelle nella finestra

**Lettore**
- Forma d'onda, casuale vero, ripeti tutto / ripeti brano, dissolvenza da 1 a 12 secondi
- Velocità da 0,5× a 2×, cambiando o no anche il tono
- Volume normalizzato (−14 LUFS) ed equalizzatore a 10 bande con preset e curva di risposta
- **Testi sincronizzati**, trovati da soli: la riga cantata si illumina sui colori della copertina, un clic su una riga
  ci salta; sopra un video diventano sottotitoli
- Tasti multimediali, controlli multimediali del sistema (Windows, MPRIS su Linux, "In riproduzione" su macOS e iPhone,
  la schermata di blocco su Android) e icona vicino all'orologio: chiudi la finestra e la musica continua usando
  pochissima memoria
- I brani con video lo mostrano nella schermata del brano, su uno sfondo sfocato di sé stesso; *Cerca il video…* ne
  trova uno su YouTube per gli altri

**Successivi**
- Trascina per riordinare, doppio clic per saltare, lascia sul cestino per togliere
- Coda automatica, *Genera* e *Svuota*, pannello da nascondere di lato
- Brani simili trovati online dopo un brano, o i brani dell'artista: si scaricano poco prima del loro turno (quanti in
  anticipo lo scegli tu), e li tieni con il tasto destro
- Passaggio facoltativo a tempo del brano dopo: quello in riproduzione si porta ai suoi BPM senza cambiare tono
- Ritrovi tutto com'era quando riapri l'app

**Libreria**
- **Brani nel cloud** accanto a quelli salvati: una libreria grande senza riempire il disco, salvata sul dispositivo
  solo dove vuoi tu (*Solo sul dispositivo*, *Salva tutti quelli nel cloud*)
- Playlist con copertina personalizzata, Preferiti, ascoltati di recente e la vista *Senza playlist* per fare ordine
- Tag colorati, filtri con più tag (tutti / almeno uno) e `#tag` nella ricerca
- Selezioni più brani, playlist o download in qualsiasi lista e agisci su tutti insieme
- *Elimina…* toglie solo il video, solo il testo, l'audio dal dispositivo o il brano intero, e mostra lo spazio
  liberato
- **Pacchetti `.ump`**: una playlist, un tag, più di uno o tutto in un solo file, con l'audio o con i soli link
  (abbastanza piccolo per Discord); lo apri o lo trascini sull'app per importarlo, senza doppioni dei brani che hai già
- **MP3 in una cartella** per una chiavetta, una scheda SD o l'auto: "Artista - Titolo.mp3" con tag e copertina
- Rinomina un artista su tutti i suoi brani in un colpo; modifica titolo, artista, album, BPM e copertina
- Statistiche di ascolto per profilo: ascolti, tempo e ultimo ascolto di ogni brano (anche quelli ascoltati nelle stanze
  e non salvati), i meno ascoltati per fare pulizia

**DJ**
- Due tracce con forme d'onda scorrevoli e panoramica, CUE, avanti/indietro, range del tempo ±8 / 16 / 50 %, key lock
- Un selettore dei brani per ogni traccia: libreria, playlist, tag, il brano in riproduzione o un file
- BPM rilevati in automatico, TAP su ogni traccia (anche con il tasto T), griglia dei beat e SYNC di tempo e fase
- Mixer con EQ a 3 bande e kill, fader dei canali e crossfader
- Registra il mix e salvalo come MP3 nella libreria, anche in una playlist nuova

**Ascolta insieme**
- Le stanze sulla stessa rete (Wi-Fi o cavo) si trovano da sole; da lontano entrate tutti nella stessa rete di Radmin
  VPN. Una stanza può avere una password e un numero massimo di persone; si può entrare anche con l'indirizzo.
- Tutti sentono lo stesso brano allo stesso punto: comanda l'orologio dell'host, ogni computer lo segue (volume ed
  equalizzatore restano i tuoi)
- Ogni computer si procura il brano in riproduzione e i due dopo: dalla sua libreria, dalla memoria delle stanze, dal
  link del brano, oppure glielo manda qualcuno nella stanza. Un brano parte quando ce l'hanno tutti (o dopo qualche
  secondo per chi ce l'ha); chi arriva dopo entra al punto giusto.
- L'host decide chi può aggiungere, togliere e spostare, saltare, mettere in pausa, andare avanti e indietro; per gli
  altri i tasti sono oscurati. In una stanza i tasti play delle tue liste diventano **+** e il doppio clic mette il
  brano nella stanza.
- Chat, chi ha aggiunto ogni brano, l'avanzamento dei download di tutti, espulsioni, passaggio dell'host. Se l'host
  esce o il suo PC si spegne, la stanza passa a chi ha più permessi (a parità, a chi è entrato per primo).
- I brani ascoltati nelle stanze restano in memoria (10 di base, insieme ai consigliati); quelli che ti piacciono li
  salvi nella libreria o in una playlist
- Il P2P si può spegnere dalle impostazioni: i brani arrivano allora solo dalla tua libreria, dalla memoria o dal loro
  link

> [!TIP]
> La prima volta Windows chiede se l'app può usare la rete: consenti l'accesso. Con Radmin VPN spunta anche *Reti
> pubbliche*, oppure usa **Consenti nel firewall** nell'app. Radmin VPN esiste solo per Windows: con amici su Linux o
> macOS usate ZeroTier o Tailscale, che vanno ovunque (anche su Windows).

**E poi**
- La stessa app su Windows, Linux, macOS e Android (e iPhone, in anteprima)
- Profili come in Chrome, temi, italiano e inglese, animazioni e scorrimento fluidi
- Stato su Discord con brano, artista, copertina e barra di avanzamento
- Aggiornamenti automatici dalle release di GitHub

## 💬 Aiuto e segnalazioni

Hai trovato un problema o hai un'idea? Apri una [segnalazione](https://github.com/KirboHQ/UltimateMP3Player/issues),
oppure usa **Impostazioni → Aiuto e supporto** nell'app: scrive già lei la versione dell'app e di Windows.

## 🛠️ Compilare

<details>
<summary>Requisiti, comandi e struttura del progetto</summary>
<br>

Serve il .NET 8 SDK, e per l'installer Inno Setup 6. I programmi esterni vanno in `engines\` (vedi
[engines/README.md](engines/README.md)).

| Comando | Risultato |
|---|---|
| `.\build.ps1` | App portatile in `app\` e collegamento sul Desktop |
| `.\build.ps1 -Installer` | `installer\Output\UltimateMP3Player-windows-x64-setup.exe` |
| `.\build.ps1 -Release` | `dist\`: i file da allegare a una release di GitHub: `UltimateMP3Player-windows-x64-setup.exe` (più `UltimateMP3Player-windows-x64.exe`, facoltativo, per aggiornamenti più leggeri, e `legacy\UltimateMP3Player.exe`, lo stesso exe con il nome che cercano le versioni fino alla 3.6), i pacchetti per Linux e macOS e gli APK per Android (`-WindowsOnly` salta entrambi, `-NoAndroid` solo gli APK) |
| `.\build-unix.ps1` | Solo i pacchetti per Linux e macOS in `dist\`: `UltimateMP3Player-linux-x64.tar.gz`, `-linux-arm64.tar.gz`, `UltimateMP3Player-macos-x64.zip`, `-macos-arm64.zip` (`-Targets linux-x64,…` per farne solo alcuni) |
| `.\build-android.ps1` | Solo gli APK per Android in `dist\android\`: `UltimateMP3Player-android-arm64-v8a.apk`, `-armeabi-v7a.apk`, `-x86_64.apk` (`-Out <cartella>` per metterli altrove) |

L'app Android richiede il .NET 10 SDK con il workload Android (`dotnet workload install android`), l'Android SDK e un
JDK (`ANDROID_HOME` / `JAVA_HOME`, oppure `%LOCALAPPDATA%\Android\Sdk` e `\Android\jdk`). I suoi motori (Python con
yt-dlp, ffmpeg e QuickJS di [youtubedl-android](https://github.com/JunkFood02/youtubedl-android)) li scarica
`tools\android-deps.ps1`, con versione e SHA-256 fissati. La prima compilazione crea la chiave di firma in
`%USERPROFILE%\.ultimatemp3player-android`, fuori dal repository: **tienine una copia** — Android installa un
aggiornamento solo se è firmato con la stessa chiave.

Anche i pacchetti per Linux e macOS si fanno da Windows. L'app per macOS è firmata "ad hoc" (senza un certificato
Apple) con [rcodesign](https://github.com/indygreg/apple-platform-rs), scaricato una volta in `tools\bin`: i Mac Apple
Silicon non avviano app senza nessuna firma. La versione in `src\UltimateMP3Player.Avalonia\UltimateMP3Player.Avalonia.csproj`
va tenuta uguale a quella di Windows (e di Android e iPhone, in `src\UltimateMP3Player.Android\UltimateMP3Player.Android.csproj`
e `src\UltimateMP3Player.iOS\UltimateMP3Player.iOS.csproj`).

L'app per iPhone ha bisogno di Xcode, quindi la compila GitHub Actions su macOS ([.github/workflows/ios.yml](.github/workflows/ios.yml)):
quando pubblichi una release le allega `UltimateMP3Player-ios.ipa` (non firmata: la firma SideStore sul telefono) e
`sidestore-source.json`; lanciando il workflow a mano (*Actions › iOS › Run workflow*) li trovi tra gli artifact
dell'esecuzione, senza toccare le release. I suoi motori (Python per iOS da python.org, yt-dlp, certifi e LAME
compilato per l'iPhone) li scarica e li mette nell'app `tools/ios-deps.sh`, con versione e SHA-256 fissati, nello stesso
workflow. Su Windows `dotnet build src\UltimateMP3Player.iOS` con l'SDK .NET 10 e il workload iOS
(`dotnet workload install ios`) controlla il codice.

Due impostazioni in `src\UltimateMP3Player\UltimateMP3Player.csproj` (e le stesse due nel progetto Avalonia):

- `<GitHubRepo>proprietario/repo</GitHubRepo>`: dove l'app cerca gli aggiornamenti e apre le segnalazioni. L'ultima
  release deve contenere il setup; se contiene anche `UltimateMP3Player-windows-x64.exe`, gli aggiornamenti scaricano solo
  quello (70 MB invece dell'intero setup). Le versioni fino alla 3.6 conoscono solo i vecchi nomi: per qualche release
  allega anche `dist\legacy\UltimateMP3Player.exe`, così si aggiornano.
- `<DiscordAppId>…</DiscordAppId>`: l'applicazione Discord usata per lo stato.

`UMP_DATA=<cartella>` avvia una copia di prova con dati suoi, accanto a quella normale. `src\Mp3Cli` è un programma
da riga di comando per provare il core (download, import, coda, casuale).

| Cartella | Contenuto |
|---|---|
| `src\UltimateMP3Player.Core` | Download (yt-dlp, gallery-dl, ffmpeg), libreria, profili, coda, analisi, traduzioni |
| `src\UltimateMP3Player` | Interfaccia WPF, motore audio (NAudio/WASAPI), motore DJ (SoundTouch), icona, tasti multimediali, aggiornamenti, Discord |
| `src\UltimateMP3Player.Avalonia` | Linux e macOS: la stessa interfaccia disegnata con Avalonia; usa i view model, i motori audio e DJ e gran parte dei servizi di `src\UltimateMP3Player` (uscita audio con SDL3, MPRIS, "In riproduzione" di macOS, aggiornamenti) |
| `src\UltimateMP3Player.Android` | Android: le pagine per il telefono (Avalonia), con i view model, il motore audio e i pezzi delle viste di Linux/macOS in comune; il servizio del lettore con i controlli nella notifica e nella schermata di blocco (MediaSession), l'uscita audio con AudioTrack, i motori dentro l'app, la condivisione, gli aggiornamenti |
| `src\UltimateMP3Player.iOS` | iPhone e iPad: le pagine e i pezzi per il telefono dell'app Android (collegati), con l'uscita audio di iOS (AudioQueue), decodifica e tag (AudioToolbox, AVFoundation), "In riproduzione", l'app File; yt-dlp dentro l'app (Python incorporato, JavaScriptCore per YouTube), MP3 con LAME |
| `installer` | Script di Inno Setup e immagini dell'installazione (`tools\make-images.ps1` le crea da `Logo.xaml`, insieme alle icone per macOS) |

</details>

## 📁 Dove sono i tuoi dati

| Percorso | Contenuto |
|---|---|
| `Musica\Ultimate MP3 Player` | Brani salvati sul dispositivo (MP3/M4A con tag e copertina) e video |
| `%LOCALAPPDATA%\Ultimate MP3 Player` | Libreria, copertine, testi, profili, impostazioni, `errori.log` |
| `%LOCALAPPDATA%\Ultimate MP3 Player\ascolta-insieme` | Memoria temporanea: brani nel cloud, consigliati e delle stanze di *Ascolta insieme* che hai ascoltato, tenuti per la prossima volta |

Su Linux i dati sono in `~/.local/share/Ultimate MP3 Player` e su macOS in `~/Library/Application Support/Ultimate MP3 Player`
(lì ci sono anche i motori, in `engines`); i brani vanno in `~/Music/Ultimate MP3 Player`. Su Android i brani sono nella
cartella dell'app `Android/data/com.kirbohq.ultimatemp3player/files/Music` (visibile dal computer), il resto dentro
l'app. Su iPhone i brani e `errori.log` sono nell'app File (*Sul mio iPhone › Ultimate MP3 Player*), il resto dentro
l'app.

## 🙏 Software di terze parti

[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [gallery-dl](https://github.com/mikf/gallery-dl) (GPL-2.0),
[FFmpeg](https://ffmpeg.org) (GPL, build da [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds), su Linux
e macOS da [martin-riedl.de](https://ffmpeg.martin-riedl.de)),
[Deno](https://deno.com) (MIT), [NAudio](https://github.com/naudio/NAudio) (MIT),
[SoundTouch.Net](https://github.com/owoudenberg/soundtouch.net) (LGPL-2.1).
Linux e macOS: [Avalonia](https://avaloniaui.net) (MIT), [SDL3](https://www.libsdl.org) (zlib) tramite
[SDL3-CS](https://github.com/ppy/SDL3-CS) (MIT), [Tmds.DBus](https://github.com/tmds/Tmds.DBus) (MIT),
[NLayer](https://github.com/naudio/NLayer) (MIT),
[Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT) e
[Selawik](https://github.com/microsoft/Selawik) (OFL-1.1), al posto di Segoe UI e delle sue icone.
Android: gli stessi, più [youtubedl-android](https://github.com/JunkFood02/youtubedl-android) (GPL-3.0) per Python,
FFmpeg e [QuickJS](https://bellard.org/quickjs/) (MIT) compilati per Android. iPhone: Avalonia, NAudio, NLayer,
SoundTouch.Net e gli stessi caratteri e icone, yt-dlp, [Python](https://www.python.org) (PSF License),
[certifi](https://github.com/certifi/python-certifi) (MPL-2.0) e [LAME](https://lame.sourceforge.io) (LGPL).

Le loro licenze, gli avvisi di copyright e dove trovare il codice sorgente di ciascuno sono in
[licenses/THIRD-PARTY-NOTICES.txt](licenses/THIRD-PARTY-NOTICES.txt), che arriva anche insieme all'app (la cartella
`licenses`).

## 📄 Licenza

Ultimate MP3 Player è software libero, distribuito con la [GNU General Public License v3.0](LICENSE) o qualsiasi
versione successiva: puoi usarlo, studiarlo, condividerlo e modificarlo, e se condividi una versione modificata deve
restare con la stessa licenza, insieme al suo codice sorgente. Vale anche per le versioni uscite prima che la licenza
venisse aggiunta.

<sub>Scarica solo contenuti che hai il diritto di scaricare.</sub>
