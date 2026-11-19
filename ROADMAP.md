# Hallow Vault roadmap

## Done

| Version | What it added |
|---|---|
| **1.0** | Finds saves (Steam cloud folders for every account, Unity and Unreal games, a list of known games, your own folders); backs up when a game closes and once a day; skips unchanged saves; keeps the newest versions; restores any version after a safety backup; Hallow apps' settings (never webhooks or keys) |
| **1.1** | Delta Force's saves (kept inside the game folder) found; Hallow family link: pipe `HallowFamily.Vault.<user>` (ping, status, backup), `--cmd`, `status.json`; What's new after an update |
| **1.2** | Compare two versions (or a version and your saves now) before restoring; back up when Steam's app manifest shows an update waiting or starting; wait until no save file changed for 10 s |
| **1.3** | Second copy to a folder you pick (cloud-synced folder or USB drive), mirrored after each backup and pruned in step; size limits per game and for the whole vault (oldest first, never a game's newest) |
| **1.4** | Hallow Hub's game page shows Saves (last backup, Back up now, Restore); Hallow Remote: Back up now from the phone - both through the family link |
| **1.5** | One Hallow icon: pipe `open`/`menu`/`invoke`/`quit` for Hub's family tray menu; Vault's icon hidden while Hub hosts it (Use the Hallow icon, on by default); balloons through Hub `notify` |
| **1.6** | Hallow accent from `%APPDATA%\Hallow\theme.json` (live, Use the Hallow accent on by default); crash reports kept on this PC with a notice next start and `lastCrash` in status |
| **1.7** | Pin a version (never pruned, kept by size limits); save alarm (shrunk / vanished saves aren't backed up over, notify + Restore); move to a new PC (export newest + pinned with a manifest, import with path re-mapping and a safety backup) |
| **1.8** | Updates itself from GitHub (asks first, SHA-256 checked); registry saves (HKCU\Software\<developer>\<game>) as .reg in the backup, restored only into that key; Documents\<game> and developer-named AppData folders for games nothing else finds |

## Next

### 1.9 - Schedules, filters and a log
- Back up every N hours, on top of game-closed and daily
- Compression level (smaller or faster)
- Include / leave out patterns per game
- Backup log: what was backed up, skipped, refused or failed, and when

### 1.10 - Second copy you can trust
- The second copy shows when it's behind (drive missing, copies not made yet) and catches up by itself
- Notices cloud sync conflict copies (OneDrive / Dropbox / Google Drive) in the second copy

### Gaps vs Ludusavi
Ludusavi (and GameSave Manager) do these; Vault doesn't yet. Buildable here = can be built and tested on this PC without the user.
- **Game coverage (the big one):** Ludusavi knows save locations for ~19,000 games from its manifest, built from PCGamingWiki. **Needs the user** - Vault would have to download the manifest (see the notes below); 1.8 narrows the gap with better guessing.
- **Registry saves:** buildable here - 1.8.
- **Scheduled backups:** buildable here - 1.9 (Ludusavi leaves scheduling to Task Scheduler; GameSave Manager makes scheduled tasks).
- **Compression choice:** buildable here - 1.9 (zip level; Ludusavi also offers other zip methods, which Windows' built-in zip can't make).
- **Filters / exclusions per game:** buildable here - 1.9.
- **Cloud upload (rclone, many providers):** not buildable here - needs accounts and a downloaded tool; Vault's second copy into a synced folder covers OneDrive / Google Drive / Dropbox. 1.10 makes it trustworthy (behind / conflicts).
- **Differential backups (full + changes):** buildable, later - Vault's per-version zips are simpler to restore and saves are small.
- **Backup validation (check every zip opens and matches):** buildable, later.
- **Redirects (restore to a different folder or drive):** partly done - 1.7's import re-maps folders between PCs.
- **Custom games / your own entries:** done - Add folder on any game; adding games that no launcher knows is buildable, later.
- **Launch wrapping (back up after a game run from a launcher):** not needed - Vault watches running games itself.
- **Steam screenshots, Proton / Wine, Steam Deck, Heroic / Lutris:** not buildable here - Windows-only app, no Linux PC to test on.
- **CLI with JSON output:** partly done - `--cmd status` gives JSON through the running app.
- **GameSave Manager's sync links (move saves into Dropbox with a link):** not planned - moving live save folders is risky; the second copy does the same job safely.

#### Ludusavi manifest notes (to ask the user before anything is downloaded)
- Where: `github.com/mtkennerly/ludusavi-manifest`, branch `data`, file `data/manifest.yaml` - about **17.8 MB** of YAML (GitHub API size 17,803,965 bytes), ~19,000 games.
- Format: top-level keys are game names; each has `files` (path templates with `<base>`, `<root>`, `<game>`, `<home>`, `<winAppData>`, `<winLocalAppData>`, `<winDocuments>`, `<storeUserId>` ... and `tags: [save, config]`, `when: [{os, store}]`), `registry` (keys with tags), `installDir`, `steam: {id}`. Schema in `data/schema.yaml`.
- License: the repo is **MIT** (code and compiled manifest). The data comes from **PCGamingWiki**, whose content is listed as **CC BY-NC-SA 3.0** (non-commercial, share-alike, attribution) - fine for a free personal app with credit; I couldn't open PCGamingWiki's own copyright page (HTTP 403), so that comes from Wikipedia's listing.
- How Vault could use it: on the user's OK, download it once (and check for a newer one weekly, with its SHA from GitHub), parse the YAML into a cached index by Steam id and name (the Windows paths only), and use it as one more source in Find. Needs a small YAML reader (no YAML library in .NET Framework).
### Later
- Fixes from real use: check the game-closed backup after real game sessions
