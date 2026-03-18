# JellyTube & JellyTubbing – Jellyfin Plugins

Zwei Jellyfin-Plugins rund um YouTube:

| Plugin | Zweck |
|---|---|
| **JellyTube** | YouTube-Videos herunterladen und in die Mediathek speichern |
| **JellyTubbing** | YouTube-Videos direkt in Jellyfin streamen (ohne Download) |

---

## JellyTube

Lädt YouTube-Videos und Playlists direkt in die Mediathek. Nutzt [yt-dlp](https://github.com/yt-dlp/yt-dlp) für den Download und erstellt automatisch NFO-Metadaten sowie Vorschaubilder.

### Features

- Videos und Playlists per URL herunterladen
- Metadaten-Vorschau vor dem Download
- NFO-Dateien und Vorschaubilder automatisch generieren
- Nach Kanal in Unterordner sortieren
- Untertitel herunterladen (mehrere Sprachen)
- Download-Warteschlange mit Fortschrittsanzeige, Laufzeitanzeige und sofortigem Start
- Aktive und wartende Jobs einzeln abbrechen
- Abgeschlossene Jobs per Knopfdruck leeren
- Jellyfin-Bibliothek nach Download automatisch aktualisieren
- Geplante Playlist-Downloads per Scheduled Task
- Pro geplantem Download: eigenes Maximalalter und „Gesehen löschen"-Option
- Archiv für geplante Downloads – bereits heruntergeladene Videos werden übersprungen
- Archiv zurücksetzen direkt im Plugin möglich
- Smarter Kanal-Scan: stoppt beim ersten bereits archivierten oder zu alten Video (`--break-on-existing`, `--break-on-reject`)
- Standard-Audiosprache konfigurierbar (ISO 639-2, z. B. `deu`) – wird als Sprachmetadaten in die Audiodatei eingebettet
- Verwaiste yt-dlp-Prozesse werden beim Neustart automatisch beendet
- yt-dlp und ffmpeg Verfügbarkeitscheck in den Einstellungen
- Vollständig auf Deutsch

### Einstellungen

| Einstellung | Beschreibung |
|---|---|
| Download-Pfad | Basisverzeichnis für heruntergeladene Videos |
| yt-dlp Programmpfad | Optionaler vollständiger Pfad zur yt-dlp-Binary |
| ffmpeg Programmpfad | Optionaler vollständiger Pfad zur ffmpeg-Binary |
| Videoformat | Voreinstellung oder benutzerdefinierter yt-dlp Format-String |
| Bevorzugter Container | MP4, MKV oder WebM |
| Max. gleichzeitige Downloads | 1–10 |
| Nach Kanal sortieren | Erstellt Unterordner pro Kanal |
| Untertitel herunterladen | Inkl. Sprachauswahl |
| NFO-Dateien schreiben | Metadaten für Jellyfin |
| Vorschaubilder herunterladen | Thumbnails speichern |
| Bibliothek aktualisieren | Scan nach Download |
| Standard-Audiosprache | ISO 639-2 Sprachcode (z. B. `deu`), der in die Audiometadaten eingebettet wird |
| Geplante Downloads | Playlists automatisch prüfen, inkl. Maximalalter und „Gesehen löschen" pro Eintrag |
| Max. Videoalter (Playlist) | Globales Limit: nur Videos der letzten N Tage herunterladen |
| Gesehene Videos löschen | Nur für geplante Downloads: Datei nach dem Schauen löschen, kein erneuter Download |

---

## JellyTubbing

Streamt YouTube-Videos direkt in Jellyfin – ohne Download.

- **Trending-Kanal:** YouTube-Trending-Videos nach Region und Kategorie direkt in Jellyfin durchsuchen und abspielen
- **Abo-Synchronisation:** Abonnierte Kanäle werden als `.strm`-Dateien in eine Jellyfin-Bibliothek synchronisiert und sind dort durchsuchbar und abspielbar

Stream-URLs werden bei jedem Abspielen frisch über [yt-dlp](https://github.com/yt-dlp/yt-dlp) aufgelöst – keine Vorablösung, keine abgelaufenen Links.

### Features

- Trending-Videos nach Region (DE, AT, CH, US, …) in Jellyfin browsbar
- Kategorien: Trending, Musik, Gaming, Nachrichten, Filme
- Abonnierte Kanäle mit Google-Konto verbinden und als Bibliothek synchronisieren
- Automatischer Bibliotheks-Scan nach jedem Sync
- Sync-Zeitplan über Jellyfin-Aufgabenplanung konfigurierbar (Standard: alle 24 h)
- Manueller Sync-Trigger direkt im Plugin
- Google-Verbindung per Device Authorization Grant (kein Redirect-URI, kein öffentlicher Server nötig)
- Client-Secret per JSON-Datei importieren
- yt-dlp-Verfügbarkeitscheck in den Einstellungen
- Stream-Qualität konfigurierbar (360p–1080p)

### Einstellungen

| Einstellung | Beschreibung |
|---|---|
| YouTube Data API Key | Erforderlich für Trending und Kanal-Videos |
| OAuth2 Client-ID / Secret | Erforderlich für Abo-Synchronisation |
| STRM-Ausgabeordner | Zielordner für `.strm`-, `.nfo`- und Thumbnail-Dateien |
| Max. Videos pro Kanal | Wie viele Videos pro Kanal beim Sync geholt werden (Standard: 25) |
| yt-dlp Programmpfad | Optional – leer lassen wenn yt-dlp im PATH liegt |
| Bevorzugte Qualität | Stream-Auflösung (360p, 480p, 720p, 1080p) |
| Trending-Region | ISO 3166-1 alpha-2 Ländercode (z. B. DE, US, GB) |

---

### Einrichtung Schritt für Schritt

#### 1. YouTube Data API Key erstellen

1. [Google Cloud Console](https://console.cloud.google.com/) öffnen
2. Neues Projekt anlegen (oder bestehendes wählen)
3. **APIs & Dienste → Bibliothek → „YouTube Data API v3"** aktivieren
4. **APIs & Dienste → Anmeldedaten → Anmeldedaten erstellen → API-Schlüssel**
5. Den generierten Key im Plugin unter **YouTube Data API Key** eintragen

#### 2. OAuth2-Credentials für Abo-Synchronisation erstellen

> Nur nötig, wenn abonnierte Kanäle synchronisiert werden sollen.

1. [Google Cloud Console](https://console.cloud.google.com/) → **APIs & Dienste → OAuth-Zustimmungsbildschirm**
   - Typ: **Extern**
   - App-Name, Support-E-Mail und Entwickler-E-Mail ausfüllen
   - Unter **Scopes** → `youtube.readonly` hinzufügen
   - Unter **Testnutzer** die eigene Google-E-Mail-Adresse eintragen
2. **Anmeldedaten → Anmeldedaten erstellen → OAuth-Client-ID**
   - Anwendungstyp: **Fernseher und eingeschränkte Eingabegeräte**
   - Beliebigen Namen vergeben
3. Client-ID und Client-Secret notieren (oder JSON-Datei herunterladen)

#### 3. Credentials im Plugin eintragen

Im Jellyfin Dashboard → **Plugins → JellyTubbing → Einstellungen**:

- **JSON importieren:** Heruntergeladene `client_secret_*.json` direkt hochladen – Client-ID und Secret werden automatisch eingetragen
- Oder Client-ID und Client-Secret manuell eintragen

#### 4. Mit Google verbinden

1. **Mit Google verbinden** klicken
2. Es erscheint ein Code und eine URL (`accounts.google.com/device`)
3. Die URL in einem beliebigen Browser öffnen, den Code eingeben und mit dem Google-Konto bestätigen
4. Das Plugin erkennt die Bestätigung automatisch – Status wechselt zu **Mit Google verbunden**

#### 5. Kanäle auswählen

Nach erfolgreicher Verbindung erscheint die Liste aller abonnierten Kanäle. Kanäle, die synchronisiert werden sollen, anhaken.

#### 6. STRM-Ausgabeordner als Jellyfin-Bibliothek einrichten

> Einmalig notwendig, damit Jellyfin die synchronisierten Videos anzeigt.

1. Jellyfin Dashboard → **Mediatheken → Mediathek hinzufügen**
2. Typ: **Videos** (oder Mixed Content)
3. Ordner: den konfigurierten **STRM-Ausgabeordner** auswählen
4. Speichern und Bibliothek scannen lassen

#### 7. Synchronisieren

- **Jetzt synchronisieren** im Plugin klickt – speichert die Einstellungen und startet den Sync sofort
- Oder den Zeitplan unter **Dashboard → Geplante Aufgaben → JellyTubbing → Kanal-Synchronisation** konfigurieren (Standard: täglich)

Nach dem Sync erscheinen die Videos automatisch in der Jellyfin-Bibliothek (Ordner pro Kanal).

---

## Installation

### 1. Repository hinzufügen

In Jellyfin:
**Admin Dashboard → Plugins → Repositories → Hinzufügen**

Repository-URL:
```
https://raw.githubusercontent.com/b00namd/JellyYT/master/dist/manifest.json
```

### 2. Plugins installieren

**Admin Dashboard → Plugins → Katalog → JellyTube / JellyTubbing → Installieren**

### 3. Jellyfin neu starten

Nach der Installation muss Jellyfin neu gestartet werden.

---

## Docker Compose – Schnellstart

> **Hinweis:** JellyTubbing benötigt kein Invidious mehr. Für JellyTubbing reicht Jellyfin allein.
> Die nachfolgende Vorlage enthält Invidious nur noch für JellyTube (direktes Streaming als Alternative zu yt-dlp).

Wer Jellyfin noch nicht betreibt, kann mit dieser Vorlage starten:

```yaml
services:

  jellyfin:
    image: lscr.io/linuxserver/jellyfin:latest
    container_name: jellyfin
    restart: unless-stopped
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Europe/Berlin
    volumes:
      - ./jellyfin/config:/config
      - /media:/media                              # Mediathek / JellyTube-Downloads
      - /usr/local/bin/yt-dlp:/usr/local/bin/yt-dlp:ro   # yt-dlp auf dem Host
      - /usr/local/bin/ffmpeg:/usr/local/bin/ffmpeg:ro    # ffmpeg auf dem Host
    ports:
      - "8096:8096"
    # GPU-Transcoding (optional, nur wenn eine NVIDIA-Karte vorhanden ist):
    # deploy:
    #   resources:
    #     reservations:
    #       devices:
    #         - driver: nvidia
    #           count: all
    #           capabilities: [gpu, video, compute, utility]

  invidious:
    image: quay.io/invidious/invidious:latest
    container_name: invidious
    restart: unless-stopped
    ports:
      - "3000:3000"
    environment:
      INVIDIOUS_CONFIG: |
        db:
          dbname: invidious
          user: kemal
          password: kemal
          host: invidious-db
          port: 5432
        check_tables: true
        hmac_key: "HIER_EINEN_LANGEN_ZUFAELLIGEN_STRING_EINTRAGEN"
        invidious_companion_key: "COMPANION_SECRET_HIER_EINTRAGEN"
        invidious_companion:
          - private_url: "http://companion:8282/companion"
    depends_on:
      - invidious-db
      - companion

  companion:
    image: quay.io/invidious/invidious-companion:latest
    container_name: companion
    restart: unless-stopped
    environment:
      - SERVER_SECRET_KEY=COMPANION_SECRET_HIER_EINTRAGEN   # muss mit invidious_companion_key übereinstimmen
    volumes:
      - companion-cache:/var/tmp/youtubei.js:rw
    cap_drop:
      - ALL
    read_only: true
    security_opt:
      - no-new-privileges:true

  invidious-db:
    image: docker.io/library/postgres:14
    container_name: invidious-db
    restart: unless-stopped
    volumes:
      - invidious-db-data:/var/lib/postgresql/data
    environment:
      POSTGRES_DB: invidious
      POSTGRES_USER: kemal
      POSTGRES_PASSWORD: kemal

volumes:
  invidious-db-data:
  companion-cache:
```

> **Vor dem Start anpassen:**
> - `hmac_key` und `invidious_companion_key` / `SERVER_SECRET_KEY` durch eigene, zufällige Strings ersetzen (beide Werte müssen übereinstimmen)
> - Pfade für `yt-dlp` und `ffmpeg` an die tatsächlichen Installationsorte auf dem Host anpassen
> - `TZ` auf die eigene Zeitzone setzen

**Starten:**
```bash
docker compose up -d
```

**Dienste:**
| Dienst | URL |
|---|---|
| Jellyfin | `http://<server-ip>:8096` |
| Invidious | `http://<server-ip>:3000` |

**JellyTubbing-Einstellung – Invidious-URL:**
- `http://invidious:3000` – wenn Jellyfin im selben Docker-Netzwerk läuft
- `http://<server-ip>:3000` – wenn von außen erreichbar sein soll

---

## Voraussetzungen

- Jellyfin 10.9.x oder neuer
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) auf dem Server installiert (für beide Plugins)
- [ffmpeg](https://ffmpeg.org/) für Containerkonvertierung (mp4/mkv) – nur für JellyTube
- YouTube Data API Key – für JellyTubbing (Trending + Kanal-Sync)
- Google OAuth2-Credentials – für JellyTubbing (Abo-Synchronisation)

yt-dlp und ffmpeg können entweder im Systempfad (PATH) liegen oder der vollständige Pfad wird in den Plugin-Einstellungen angegeben.

---

## Selbst bauen

```powershell
# Beide Plugins bauen, ZIPs und Manifest erstellen
.\build.ps1
```

---

## Hinweis

Diese Plugins ermöglichen den Zugriff auf YouTube-Inhalte. Das Herunterladen und Streamen von Videos kann gegen die Nutzungsbedingungen von YouTube verstoßen. Die Nutzung erfolgt auf eigene Verantwortung und sollte ausschließlich für den persönlichen Gebrauch erfolgen.
