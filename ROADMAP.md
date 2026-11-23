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
| **1.9** | Back up every 1-12 hours; compression level; include / leave-out patterns per game; backup log |
| **1.10** | Second copy status (up to date / behind / unreachable), automatic catch-up, cloud sync conflict copies spotted |
| **1.11** | Online save database: Ludusavi's manifest (from PCGamingWiki) downloaded and re-checked weekly, parsed by a small YAML reader into a cached index; its paths and HKCU keys go first, Vault's finders add the rest |
| **1.12** | Check backups (every zip opens, CRC-32s and sizes match, the file list recorded in each backup's vault.json matches; green / amber / red per game; weekly check, off by default; read-only); Add a game (name, save folders, optional .exe for the game-closed backup; editable, removing keeps its backups) |
| **1.13** | Second copy to any folder or NAS share (typed `\\NAS\share` paths), every copy checked by SHA-256 (`sha256.txt`), its own retention, Verify copies (Settings, weekly, pipe `verify`; damaged copies replaced from the vault); restore Preview (sizes and times per file, what a restore would do) and Restore file (one file, after a safety backup); a built-in list of common save places (My Games, Saved Games, LocalLow\<company>, AppData); Steam Cloud shown per game; backup health in status.json (`lastBackupAt`, `lastBackupOk`, `failingGames`, per-game `lastResult`) |
| **1.14** | Light and dark mode (Hallow family spec): theme.json `mode` dark / light / system (Windows' AppsUseLightTheme, followed live), Settings > Look (Follow Hallow / Light / Dark) wins; the family light palette (same resource keys), accent darkened for text / icons (>= 4.5:1 on white), filled buttons keep the accent with the better of dark / white text; softer shadow; title bars follow; live through the theme.json watcher; `status` has `look` |
| **1.16** | Hallow family backup: every Hallow app's settings, Hallow Keys' and Hallow Authenticator's encrypted backups (opaque: copied and SHA-256 checked, never opened) and optionally Clips favourites, to one or more destinations (another drive, USB, NAS share on a private address), daily and after changes, newest 14 versions (pruned to the Recycle Bin / "Removed versions"), destinations with free space, last success and last error (Ransomware protection explained, Guard 1.10's helper), restore by date and per app (apps asked to quit through their pipe first, replaced files kept; Keys / Authenticator backups back into their own restore folders), Test restore, `status.family`, pipe `backup-now`, Hub menu items, a 3-day quiet note |
| **1.15** | Light mode readability fixes: the family accent rule (Toolkit 5.16) - text accent >= 4.5:1 on #EEEAF5, filled accents with the better text colour or a darker fill with white text (violet), hover x0.9; green / amber status text #1B7A44 / #8F5B00 (icons keep #1E8E4F / #A66A00); open windows recoloured live on a look or accent change (theme styles, theme brushes and Vault's own brushes swapped in place); contrast test over every light pair and several accents |

## Next

### Gaps vs Ludusavi
Ludusavi (and GameSave Manager) do these; Vault doesn't yet. Buildable here = can be built and tested on this PC without the user.
- **Game coverage (the big one):** Ludusavi knows save locations for ~19,000 games from its manifest, built from PCGamingWiki. **Approved by the user - 1.11** uses the manifest.
- **Registry saves:** buildable here - 1.8.
- **Scheduled backups:** buildable here - 1.9 (Ludusavi leaves scheduling to Task Scheduler; GameSave Manager makes scheduled tasks).
- **Compression choice:** buildable here - 1.9 (zip level; Ludusavi also offers other zip methods, which Windows' built-in zip can't make).
- **Filters / exclusions per game:** buildable here - 1.9.
- **Cloud upload (rclone, many providers):** not buildable here - needs accounts and a downloaded tool; Vault's second copy into a synced folder covers OneDrive / Google Drive / Dropbox. 1.10 makes it trustworthy (behind / conflicts); 1.13 adds NAS shares, SHA-256 checked copies, Verify and its own retention.
- **Differential backups (full + changes):** buildable, later - Vault's per-version zips are simpler to restore and saves are small.
- **Backup validation (check every zip opens and matches):** buildable here - 1.12 (Check backups: opens, CRC-32s, the recorded file list; red / amber / green; weekly, off by default; read-only).
- **Redirects (restore to a different folder or drive):** partly done - 1.7's import re-maps folders between PCs.
- **Restore one file / preview a restore:** buildable here - 1.13 (Preview with sizes and times, Restore file after a safety backup).
- **Custom games / your own entries:** done - Add folder on any game; 1.12 adds games no launcher knows (Add a game: name, save folders, optional .exe).
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
- ~~Light mode: green / amber status text is 3.8-4.5:1 on the light panels; dialogs open during a live switch keep their look until they're opened again~~ - done in 1.15
- NAS second copy live: the user's UGREEN NAS is paused for now - when it's back, type its share in Settings > Second copy (nothing NAS-specific is needed) - and add it as a Hallow family backup destination (Family backup > Add)
- Hallow family backup: restore onto a new PC from another PC's versions (listed already; settings paths are re-mapped by %APPDATA% / %LOCALAPPDATA%, Keys' backup folder is the new PC's)
- Steam Cloud: per-game cloud on/off from Steam's own settings (now: a synced file in any account's cloud cache)
- Dedupe between versions (store unchanged files once) - only if vaults grow big; saves are small and plain zips are simpler to restore
