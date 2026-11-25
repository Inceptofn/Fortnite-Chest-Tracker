# Chest Tracker

A free, open-source desktop app that reads your Fortnite replays and counts the chests you open
each match — then shows the count live on your stream as you loot. Everything runs on your PC; no
account, no uploads.

Built for Chapter 7, the Treasure Hunters season. Site: [chesttracker.app](https://chesttracker.app).

## What it does

- **Counts the chests you open**, per match, from the replay files Fortnite already saves. Searched
  chests and ones you broke open both count; a refilled chest you open again counts again.
- **Live counter for streamers.** While you play, it reads the game's own "+XP CHEST" popup off the
  screen with Windows OCR and ticks the count up in real time. No game hooks, no memory reading — it
  only looks at the screen, so it's anticheat-safe, and because it keys on your own XP it only counts
  your chests.
- **Two overlays.** A transparent in-game overlay that sits over Fortnite, and a browser source for
  OBS/Streamlabs. Both show the live count plus your average and last game, in a color and title you set.
- **Lifetime stats.** Totals carry across runs and survive Fortnite's replay rotation; matches are
  de-duplicated by match id, so nothing is counted twice. Filter by last 24 hours, 7 days, the season,
  or all time.
- **Your squad's chests.** Each teammate's count per match, plus who you play with most. These are a
  floor, not an exact figure — see the limitations below.
- **Treasure-hunt pulls.** When a chest pays out a treasure-hunt prize, the server spawns a golden
  chest carrying the prize id and who opened it. The tracker reads that, so it can tell you who
  pulled what, the Pirate Shop Access token included.
- **Gizmos.** A gizmo goes to everyone in the squad. Gizmos have no golden chest, but each one is
  granted through an ability on your player; the tracker reads those grants, so it sees every
  gizmo, usually with its name and whose chest it came out of.
- **Odds and Treasure Hours.** Epic's per-chest odds for every treasure, this week's items, your
  chance of having hit each one by now, and a countdown to the next Treasure Hour. While one is
  live the table switches to the boosted rates.
- **Player stats.** Click a teammate's name (in *Squad, last game* or *Most played with*) to open
  their chests per match, with the same bar chart, best/last game, average, a game-by-game list and
  the treasures that came out of their chests.
- **Earlier matches.** Type your total matches and press **Update counter** to apply the estimate to
  the main screen and the overlay (typing alone only previews it).
- **Adaptive window.** The layout follows the window size: sidebar beside the dashboard when wide,
  one scrolling column when narrow, with the stats, squad panels and chart adjusting to fit.

## Only Battle Royale counts

The tracker counts **normal Build** (`Playlist_Default*`) and **Zero Build** (`Playlist_NoBuildBR_*`)
Battle Royale only. Ranked, Reload, OG, Creative/UEFN, LEGO, Festival and limited-time modes are read
and skipped — the scan report tells you how many were left out.

## Requirements

- Windows 10/11.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.
- Fortnite, with replays enabled in Settings. Replays live in
  `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. Fortnite keeps about 100 recent replays and rotates the
  oldest out every few hours, so run the app regularly to catch matches before they roll off.
- An OCR language in Windows (Settings → Time & Language → Language) for the live counter. English is
  installed by default. For Spanish, add it there and keep **Optical character recognition** ticked
  in its language options. The live counter can only read a language Windows has installed.

## Run it

From the repository root:

```
dotnet run -c Release --project src/ChestCounter.Gui
```

That launches the dashboard. The live counter and the overlay server start with it. Play Fortnite in
**borderless windowed** so the live counter can see the screen.

To add the overlay to OBS or Streamlabs, add a **Browser Source** pointing at `http://localhost:7777/`
(around 320×150, transparent background). It shows the live count during a match and your lifetime
average between matches; the exact per-match total from the replay reconciles it after each game.

Lifetime stats are saved to `%APPDATA%\ChestCounter\stats.json`. Settings are in `overlay.json`
(title, colour, opacity), `app.json` (window and in-game overlay) and `tracker.json` (OCR language,
chest label and your match total), all in the same folder.

## Settings

In the overlay panel:

- **Show in-game overlay** — a draggable, always-on-top card over the game. Drag it where you want it,
  then lock it so clicks pass through to Fortnite.
  - `Ctrl+Shift+O` — show/hide
  - `Ctrl+Shift+L` — lock/unlock (click-through)
- The window can be resized; the chart takes up the extra room.
- **Minimize to tray** — minimizing hides the window to the system tray instead of the taskbar.
  Double-click the tray icon to bring it back.
- **Accent color** and **title** apply to both the in-game overlay and the browser source.
- **Opacity** — a slider from 20% to 100% that fades the whole overlay, text included. It applies
  to the in-game overlay at once and to the OBS browser source within a couple of seconds.
- **Start with Windows** — adds a per-user entry (no admin rights) that opens the app with Windows,
  hidden in the tray. Untick it to remove the entry. If you move the exe, it re-points itself to the
  new location the next time you run it.

In the **Live counter language** panel:

- **English / Español** — the language Fortnite is set to. It picks the Windows OCR language and the
  word the counter looks for under the XP popup (CHEST in English, COFRE in Spanish). It applies
  immediately; a match in progress keeps its count. If that OCR language is not installed in Windows
  the counter falls back to your profile language and the panel says so.
- **Chest label (optional)** — the exact word your game shows under the XP, for when the built-in
  word is not what your client displays. The status bar shows the last popup the OCR read, which is
  the quickest way to find out what to type.

In the **Earlier matches** panel:

- **Total matches** — how many Battle Royale matches you have played in total. The replays only go
  back about 100 matches, so the rest are estimated as
  `(your total − matches read from replays) × your average chests per match`. The result is added to
  the **Opened** figure when the **All time** filter is selected (it shows "incl. ~N est."), and the
  panel breaks it down. Matches read from replays are never changed. As more replays are read the
  estimate shrinks, since fewer matches are left uncovered.

## How it works

- Parses replays with a patched copy of [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)
  by Shiqan, kept in `lib/`. The patches are LED-N's, from
  [Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter); `lib/PATCHES.md` lists them.
  Both are MIT-licensed. The container, refill and gizmo-grant handling follows LED-N's analyzer.
- Any chest blueprint the parser does not know is read as a generic container and classified by
  name, so new chest types are picked up without a code change. Loot cannons, ammo boxes and food
  boxes are not chests.
- A closed chest destroyed with nobody named is matched to a player by position: the server makes
  the chest the owner of the loot it throws out, so the loot marks the spot.
- A chest you opened that drops out of range and comes back is recognised as the same chest and
  not counted again; a chest that was refilled and opened again is.
- Attributes each opened chest to you through the chest's `SearchingPawn`, resolved to a player
  along the replay's actor → pawn → player-state chain, matched against the recording player.
- Already-scanned replays are skipped by name and size, so repeat scans are fast.

## Honest limitations

- A replay only records what was network-relevant to you, and XP-giving chest searches are the signal
  it reads most reliably. The live count and the replay total line up after each match.
- Teammate counts are shown as "≥": a chest a squadmate opens far away from you is never written to
  your replay, so their real number can be higher.
- A chest that breaks without the game naming who did it (its floor was shot out, say) goes to
  whoever stood within 4.5 m of the loot it spilled. If nobody was that close, it is not counted.
- Treasure Hours follow the pattern so far (Saturday 19:00 UTC and Sunday 02:00 UTC, two hours
  each). Epic announces each one, so future times are expected rather than confirmed.
- Golden-chest prizes (the token, outfits, decals and so on) are read the same way other trackers
  read them, but none of the replays this was developed against contained one, so that part is
  untested until a pull shows up.
- A live replay is encrypted with a placeholder key until the match finalizes, so the replay can't be
  read mid-game. The live counter is the OCR path; the exact numbers come from the replay afterward.

## Project layout

```
src/
  ChestCounter.Core/   replay parsing, chest attribution, stats, overlay server
  ChestCounter.Ocr/    screen capture + Windows OCR live counter
  ChestCounter.Gui/    the Windows dashboard (ChestCounter.exe)
  ChestCounter.Live/   optional headless live counter + overlay
lib/                   the patched replay parser (see lib/README.md)
```

## License

MIT — see [LICENSE](LICENSE). The replay parser in `lib/` carries its own MIT notices
(`lib/LICENSE-FortniteReplayDecompressor`, `lib/LICENSE-LED-N-ChestCounter`). Not affiliated with Epic Games. Fortnite is a trademark of Epic Games, Inc.
