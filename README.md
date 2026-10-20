# JellyTube – Jellyfin Plugin

Lädt YouTube-Videos, Playlists und Kanäle in die Jellyfin-Mediathek – manuell oder automatisch per Zeitplan. Nutzt [yt-dlp](https://github.com/yt-dlp/yt-dlp) für den Download (das Plugin hält yt-dlp selbst aktuell) und erstellt NFO-Metadaten, Vorschaubilder und Kapitel.

---

## Features

**Download**
- Videos, Playlists und Kanäle per URL herunterladen, mit Metadaten-Vorschau
- Qualitätsprofile von 480p bis 4K (H.264 + AAC bevorzugt für Direct-Play), nur Audio oder eigener Format-String
- YouTube-Kapitel einbetten, Untertitel herunterladen (mehrere Sprachen)
- Standard-Audiosprache (ISO 639-2, z. B. `deu`) wird in die Audiospur eingebettet
- Shorts bei Kanal-Downloads ausschließen
- Cookies-Datei für Inhalte, die eine Anmeldung erfordern
- Automatische Wiederholung fehlgeschlagener Downloads, erzwungenes erneutes Herunterladen

**Mediathek**
- NFO-Dateien und Vorschaubilder automatisch generieren
- Nach Kanal in Unterordner sortieren
- Kanäle als Serien: automatische Downloads landen in „Season 1“, manuell geladene Playlists werden eine eigene Staffel
- Jellyfin-Bibliothek nach dem Download automatisch aktualisieren

**Automatisierung**
- Geplante Playlist-/Kanal-Downloads per Scheduled Task, pro Eintrag mit Zielordner, Maximalalter und „Gesehen löschen“
- Download-Archiv: bereits geladene Videos werden übersprungen; Archiv zurücksetzen oder mit den vorhandenen Dateien abgleichen
- Smarter Kanal-Scan mit einstellbarer Scantiefe: stoppt beim ersten bereits archivierten oder zu alten Video
- Gesehene Videos nach einer Karenzzeit automatisch löschen – egal ob manuell oder von Jellyfin automatisch als gesehen markiert
- yt-dlp wird wöchentlich automatisch aktualisiert (Scheduled Task „Update yt-dlp Binary“)

**Oberfläche**
- Download-Warteschlange mit Fortschritts- und Laufzeitanzeige, Jobs einzeln oder alle abbrechen
- Verfügbarkeitscheck für yt-dlp und ffmpeg in den Einstellungen
- Verwaiste yt-dlp-Prozesse werden beim Neustart automatisch beendet
- Vollständig auf Deutsch

---

## Einstellungen

| Einstellung | Beschreibung |
|---|---|
| Download-Pfad | Basisverzeichnis für heruntergeladene Videos |
| yt-dlp Programmpfad | Optional; leer = vom Plugin verwaltetes yt-dlp, sonst yt-dlp aus dem PATH |
| ffmpeg Programmpfad | Optional; leer = ffmpeg aus dem PATH (im Jellyfin-Docker-Image: `/usr/lib/jellyfin-ffmpeg/ffmpeg`) |
| Videoformat | Qualitätsprofil oder benutzerdefinierter yt-dlp Format-String |
| Bevorzugter Container | MP4, MKV oder WebM |
| Max. gleichzeitige Downloads | 1–10 |
| Wiederholungsversuche | Wie oft ein fehlgeschlagener Download erneut versucht wird |
| Kanal-Scantiefe | Wie viele Videos pro Lauf eines Kanals geprüft werden |
| Nach Kanal sortieren | Erstellt Unterordner pro Kanal |
| Kanäle als Serien darstellen | Kanal = Serie, Videos = Episoden (Mediathek vom Typ „Serien“ nötig) |
| YouTube-Kapitel einbetten | Kapitelmarken in die Videodatei schreiben |
| Shorts ausschließen | Shorts bei Kanal-Downloads überspringen |
| Untertitel herunterladen | Inkl. Sprachauswahl |
| Cookies-Datei | Für altersbeschränkte oder Mitglieder-Inhalte |
| Standard-Audiosprache | ISO 639-2 Sprachcode (z. B. `deu`), der in die Audiometadaten eingebettet wird |
| NFO-Dateien schreiben | Metadaten für Jellyfin |
| Vorschaubilder herunterladen | Thumbnails speichern |
| Bibliothek aktualisieren | Scan nach Download |
| Gesehene Videos löschen | Getrennt für manuelle und geplante Downloads, mit Karenzzeit in Tagen |
| Geplante Downloads | Playlists/Kanäle automatisch prüfen, pro Eintrag mit Zielordner, Maximalalter und „Gesehen löschen“ |

---

## Installation

### 1. Repository hinzufügen

In Jellyfin:
**Admin Dashboard → Plugins → Repositories → Hinzufügen**

Repository-URL:
```
https://raw.githubusercontent.com/b00namd/JellyFinPlugins/master/dist/manifest.json
```

### 2. Plugin installieren

**Admin Dashboard → Plugins → Katalog → JellyTube → Installieren**

### 3. Jellyfin neu starten

Nach der Installation muss Jellyfin neu gestartet werden.

---

## Voraussetzungen

- Jellyfin 10.9.x oder neuer
- [ffmpeg](https://ffmpeg.org/) zum Zusammenführen von Video und Audio sowie für Containerkonvertierung (mp4/mkv)

yt-dlp muss nicht installiert sein: Der Scheduled Task „Update yt-dlp Binary“ lädt es in den Plugin-Konfigurationsordner und hält es aktuell (läuft sonntags um 3 Uhr – nach der Installation einmal manuell unter **Dashboard → Geplante Aufgaben** starten). Alternativ kann ein eigenes yt-dlp im PATH oder per Pfad in den Einstellungen verwendet werden. ffmpeg muss im Systempfad (PATH) liegen oder der vollständige Pfad wird in den Plugin-Einstellungen angegeben.

---

## Selbst bauen

```powershell
# Beide Plugins bauen, ZIPs und Manifest erstellen
.\build.ps1
```

---

## Hinweis

Dieses Plugin ermöglicht den Zugriff auf YouTube-Inhalte. Das Herunterladen von Videos kann gegen die Nutzungsbedingungen von YouTube verstoßen. Die Nutzung erfolgt auf eigene Verantwortung und sollte ausschließlich für den persönlichen Gebrauch erfolgen.
