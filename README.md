# Hallow Vault

Backs up your game saves and settings - part of the Hallow family with [Hallow Hub](https://github.com/Dom428/hallow-hub), [Hallow Mixer](https://github.com/Dom428/hallow-mixer) and [Hallow Toolkit](https://github.com/Dom428/hallow-toolkit).

- **Finds your saves by itself:** Steam's per-game folders (every account on the PC), Unity games (`LocalLow\company\product` from the game's `app.info`), Unreal games (`LocalAppData\<project>\Saved`), and a few well-known places. Add any folder yourself.
- **Backs up when a game closes** (only if something changed), **right before Steam updates a game** (the app manifest says an update is waiting or starting) **and once a day**, keeping the last 10 versions per game. Never while the game is still writing: it waits until no save file has changed for 10 seconds.
- **Restore any version** - your current saves are backed up first. **Compare** shows which files were added, removed, got bigger or smaller (between two versions, or a version and your saves now).
- Backups are plain **.zip** files, on a second drive by default (or any folder / network share you pick).
- **Second copy** to another folder you pick - a OneDrive, Google Drive or Dropbox folder, or a USB drive (no accounts inside Vault). Every new backup is copied there and old ones go in step. **Size limits** per game and for the whole vault: oldest versions go first, never a game's newest.
- Leaves out logs, caches, crash dumps and secrets.
- **Hallow family link:** Hallow Hub and Hallow Remote see your backups and can start one. Named pipe `HallowFamily.Vault.<user>` (`ping`, `status`, `backup <game>`, `backup all`, `restore <game>` opens the restore window; current user only), `HallowVault.exe --cmd "backup all"` for scripts and Stream Deck, and `%APPDATA%\HallowVault\status.json`.

Built with the C# compiler that ships with Windows (.NET Framework 4.8): run `build.ps1`. No admin needed.

## Credits

Icons: [Font Awesome Free](https://fontawesome.com) 7.3.1 by Fonticons, Inc., licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (see FontAwesome-LICENSE.txt).