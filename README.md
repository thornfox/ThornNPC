# ThornNPC

Developer: ThornFox · Version: 1.0.0

[English](#english) | [Deutsch](#deutsch)

---

## English

Oxide plugin for Rust that places human NPCs with a fixed protection layer. They cannot be targeted by helicopters, Bradley APCs, animals or other NPCs, and hitting one by a player causes a penalty instead of a death.

### Features

- **Fixed protection:** Patrol helicopters, helicopter turrets, Bradley APCs, animals and NPCs do not target ThornNPCs. Not configurable.
- **Penalty:** A player who damages a ThornNPC makes it disappear for a set time and leaves a note with a configurable text. No death, no loot.
- **Patrol:** Waypoints (3 to 10 per NPC). The NPC walks the route, pauses at the end and may sit on a free chair.
- **Talk (E):** Pressing E at an NPC stops it, turns it towards the player and opens its shop. It walks on again once the player is more than 5 m away.
- **Zones:** Each NPC can greet players who enter a set radius.
- **Shop link:** Connects an NPC to categories of the Shop plugin.
- **Admin GUI:** `/npc_gui` for name, note text, penalty, zone radius and clothing.
- **Wipe handling:** Optional. On a wipe the NPC list is cleared, IDs are never reused.

### Installation

1. Copy `ThornNPC.cs` into `oxide/plugins/`.
2. The plugin loads automatically. Create NPCs with `/npc_add`.

Optional plugins: ImageLibrary (outfit icons in the GUI), Shop (shop links).

### Commands

| Command | Permission | Effect |
|---|---|---|
| `/npc_add` | thornnpc.admin | Creates an NPC at your position |
| `/npc_way <id> [clear]` | thornnpc.admin | Adds a patrol point at your position, or clears all |
| `/npc_spawn <id>` | thornnpc.admin | Moves the spawn point to your position and clears its waypoints |
| `/npc_spawn monument` | thornnpc.admin | Places all NPCs at random monuments with a short patrol |
| `/npc_tp <id>` | thornnpc.admin | Teleports you to an NPC |
| `/npc_remove <id>` | thornnpc.admin | Removes an NPC |
| `/npc_zone <id> <radius> [text]` | thornnpc.admin | Sets the greeting zone (0 turns it off) |
| `/npc_shop` | thornnpc.admin | Lists and links the shop categories of all NPCs |
| `/npc_gui [id]` | thornnpc.admin | Admin panel for NPCs within the GUI radius |
| `/npc_clean [all]` | thornnpc.admin | Removes orphaned NPC figures |
| `/callnpcs` | thornnpc.callnpcs | Calls all NPCs to you, sets new spawn points and clears their waypoints |

### Permissions

- `thornnpc.admin`: all admin commands and the admin panel
- `thornnpc.callnpcs`: `/callnpcs`

### Configuration

File: `oxide/config/ThornNPC.json`. Created on first load.

| Key | Default | Description |
|---|---|---|
| `ConfigVersion` | 1 | Config version |
| `PenaltySeconds` | 600 | Penalty time after a player hits a ThornNPC (10 s to 24 h) |
| `GuiRadius` | 10 | Radius in metres for `/npc_gui` |
| `WipeNpcsOnNewSave` | true | On a wipe, clear the NPC list |
| `NoteText` | (see file) | Text of the note that drops after a penalty |
| `MessageHello`, `MessageBye`, `MessageUse`, `MessageHurt` | (lists) | Random lines the NPCs say |
| `Npcs` | {} | Per-NPC settings (penalty, note, zone radius, zone message), filled by the admin panel |

### Dependencies

- Rust with Oxide (Oxide.Rust)
- Optional: ImageLibrary, Shop

### Changelog

- **1.0.0**: First release.

### Tested on RustRentner

All ThornFox plugins are tested extensively on [rustrentner.de](https://rustrentner.de).

### License

MIT, see [LICENSE](LICENSE).

---

## Deutsch

Oxide-Plugin für Rust, das Menschen-NPCs mit einer festen Schutzschicht platziert. Helikopter, Bradley-Panzer, Tiere und andere NPCs nehmen sie nicht ins Ziel, und wenn ein Spieler einen NPC trifft, folgt eine Strafe statt eines Todes.

### Funktionen

- **Fester Schutz:** Patrouillen-Helikopter, Heli-Geschütze, Bradley-Panzer, Tiere und NPCs wählen ThornNPCs nicht als Ziel. Nicht abschaltbar.
- **Strafe:** Ein Spieler, der einen ThornNPC beschädigt, lässt ihn für eine festgelegte Zeit verschwinden. Dabei bleibt ein Zettel mit einem einstellbaren Text liegen. Kein Tod, kein Loot.
- **Patrouille:** Wegpunkte (3 bis 10 pro NPC). Der NPC geht die Route ab, pausiert am Ende und kann sich auf einen freien Stuhl setzen.
- **Gespräch (E):** Mit E bei einem NPC bleibt er stehen, dreht sich zum Spieler und öffnet seinen Shop. Er läuft weiter, sobald der Spieler mehr als 5 m entfernt ist.
- **Zonen:** Jeder NPC kann Spieler begrüßen, die in einen festgelegten Radius kommen.
- **Shop-Verknüpfung:** Verbindet einen NPC mit Kategorien des Shop-Plugins.
- **Admin-GUI:** `/npc_gui` für Name, Zettel-Text, Strafzeit, Zonenradius und Kleidung.
- **Wipe-Behandlung:** Optional. Bei einem Wipe wird die NPC-Liste geleert. IDs werden nie wiederverwendet.

### Installation

1. `ThornNPC.cs` nach `oxide/plugins/` kopieren.
2. Das Plugin lädt automatisch. NPCs legst du mit `/npc_add` an.

Optionale Plugins: ImageLibrary (Kleidungs-Icons in der GUI), Shop (Shop-Verknüpfungen).

### Befehle

| Befehl | Permission | Wirkung |
|---|---|---|
| `/npc_add` | thornnpc.admin | Legt einen NPC an deiner Position an |
| `/npc_way <id> [clear]` | thornnpc.admin | Fügt einen Wegpunkt an deiner Position hinzu, oder löscht alle |
| `/npc_spawn <id>` | thornnpc.admin | Setzt den Spawnpunkt auf deine Position und löscht seine Wegpunkte |
| `/npc_spawn monument` | thornnpc.admin | Verteilt alle NPCs auf zufällige Monumente mit kurzer Patrouille |
| `/npc_tp <id>` | thornnpc.admin | Teleportiert dich zu einem NPC |
| `/npc_remove <id>` | thornnpc.admin | Entfernt einen NPC |
| `/npc_zone <id> <Radius> [Text]` | thornnpc.admin | Setzt die Begrüßungszone (0 schaltet sie aus) |
| `/npc_shop` | thornnpc.admin | Listet und verknüpft die Shop-Kategorien aller NPCs |
| `/npc_gui [id]` | thornnpc.admin | Admin-Panel für NPCs im GUI-Radius |
| `/npc_clean [all]` | thornnpc.admin | Entfernt verwaiste NPC-Figuren |
| `/callnpcs` | thornnpc.callnpcs | Holt alle NPCs zu dir, setzt neue Spawnpunkte und löscht ihre Wegpunkte |

### Berechtigungen

- `thornnpc.admin`: alle Admin-Befehle und das Admin-Panel
- `thornnpc.callnpcs`: `/callnpcs`

### Konfiguration

Datei: `oxide/config/ThornNPC.json`. Wird beim ersten Laden angelegt.

| Schlüssel | Standard | Beschreibung |
|---|---|---|
| `ConfigVersion` | 1 | Version der Config |
| `PenaltySeconds` | 600 | Strafzeit nach einem Treffer durch einen Spieler (10 s bis 24 h) |
| `GuiRadius` | 10 | Radius in Metern für `/npc_gui` |
| `WipeNpcsOnNewSave` | true | Bei einem Wipe die NPC-Liste leeren |
| `NoteText` | (siehe Datei) | Text des Zettels, der nach einer Strafe fällt |
| `MessageHello`, `MessageBye`, `MessageUse`, `MessageHurt` | (Listen) | Zufallssprüche der NPCs |
| `Npcs` | {} | Einstellungen pro NPC (Strafzeit, Zettel, Zonenradius, Zonen-Nachricht), vom Admin-Panel gefüllt |

### Abhängigkeiten

- Rust mit Oxide (Oxide.Rust)
- Optional: ImageLibrary, Shop

### Changelog

- **1.0.0**: Erstveröffentlichung.

### Getestet auf RustRentner

Alle Plugins von ThornFox werden ausgiebig auf [rustrentner.de](https://rustrentner.de) getestet.

### Lizenz

MIT, siehe [LICENSE](LICENSE).
