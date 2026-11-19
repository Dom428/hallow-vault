# Hallow Vault

Backs up your game saves and settings - part of the Hallow family with [Hallow Hub](https://github.com/Dom428/hallow-hub), [Hallow Mixer](https://github.com/Dom428/hallow-mixer) and [Hallow Toolkit](https://github.com/Dom428/hallow-toolkit).

- **Finds your saves by itself:** Steam's per-game folders (every account on the PC), Unity games (`LocalLow\company\product` from the game's `app.info`), Unreal games (`LocalAppData\<project>\Saved`), and a few well-known places. Add any folder yourself.
- **Backs up when a game closes** (only if something changed), **right before Steam updates a game** (the app manifest says an update is waiting or starting) **and once a day**, keeping the last 10 versions per game. Never while the game is still writing: it waits until no save file has changed for 10 seconds.
- **Restore any version** - your current saves are backed up first. **Compare** shows which files were added, removed, got bigger or smaller (between two versions, or a version and your saves now).
- Backups are plain **.zip** files, on a second drive by default (or any folder / network share you pick).
- **Second copy** to another folder you pick - a OneDrive, Google Drive or Dropbox folder, or a USB drive (no accounts inside Vault). Every new backup is copied there and old ones go in step. **Size limits** per game and for the whole vault: oldest versions go first, never a game's newest.
- Leaves out logs, caches, crash dumps and secrets.
- **Hallow family:** Hallow Hub's game pages show each game's Saves (last backup, Back up now, Restore) and Hallow Remote has Back up now on your phone. The link: Named pipe `HallowFamily.Vault.<user>` (`ping`, `status`, `backup <game>`, `backup all`, `restore <game>` opens the restore window; current user only), `HallowVault.exe --cmd "backup all"` for scripts and Stream Deck, and `%APPDATA%\HallowVault\status.json`.
- **One Hallow icon:** while Hallow Hub hosts the family tray menu, Vault hides its own icon and its actions (Back up everything now, Restore..., Open the vault folder, Back up when a game closes) are in Hub's menu; its messages show from Hub's icon. Pipe commands `open`, `menu`, `invoke <id>`, `quit`. Settings > Use the Hallow icon (on by default); without Hub, Vault keeps its own icon.
- **Hallow accent:** uses the family accent colour from `%APPDATA%\Hallow\theme.json` (picked in Hallow Toolkit), live; Settings > Use the Hallow accent colour.
- **Crash reports** stay on this PC (`%APPDATA%\HallowVault\crashes`, newest 20; no settings or file contents) with a notice next time; `status` has `lastCrash`.
- **Pins:** pin a version in Restore and it's never pruned or removed by size limits (`pins.txt` next to the zips, copied with the second copy; pipe `pin <game>` / `unpin <game>`).
- **Save alarm:** if a game's saves vanish, empty or shrink under half of the last backup, Vault doesn't back them up over the good ones and tells you (through Hallow Hub's icon when it hosts it), with Restore one click away.
- **Move to a new PC:** Settings > Export copies every game's newest version (and pinned ones) with a manifest to a folder; Import on the new PC restores the ticked games after a safety backup, re-mapping your user folder, Documents, AppData, the Steam account and the game folder.
- **Registry saves:** games that keep saves or settings in `HKCU\Software\<developer>\<game>` (like Unity's PlayerPrefs) are backed up as a `.reg` file inside the zip and put back only into that key, after the usual safety backup.
- **More saves found without a list:** for games nothing else finds, `Documents\<game>` and AppData / LocalAppData / LocalLow folders named after the developer in the game's own files (big guessed folders are ignored as caches).
- **Updates itself** from GitHub releases: asks first and checks the download's SHA-256 (Settings > Check for updates).

Built with the C# compiler that ships with Windows (.NET Framework 4.8): run `build.ps1`. No admin needed.

## Credits

Icons: [Font Awesome Free](https://fontawesome.com) 7.3.1 by Fonticons, Inc., licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (see FontAwesome-LICENSE.txt).