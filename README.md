# Hallow Vault

Backs up your game saves and settings - part of the Hallow family with [Hallow Hub](https://github.com/Dom428/hallow-hub), [Hallow Mixer](https://github.com/Dom428/hallow-mixer) and [Hallow Toolkit](https://github.com/Dom428/hallow-toolkit).

- **Finds your saves by itself:** Steam's per-game folders (every account on the PC), Unity games (`LocalLow\company\product` from the game's `app.info`), Unreal games (`LocalAppData\<project>\Saved`), and a few well-known places. Add any folder yourself.
- **Backs up when a game closes** (only if something changed) **and once a day**, keeping the last 10 versions per game.
- **Restore any version** - your current saves are backed up first.
- Backups are plain **.zip** files, on a second drive by default (or any folder / network share you pick).
- Leaves out logs, caches, crash dumps and secrets.

Built with the C# compiler that ships with Windows (.NET Framework 4.8): run `build.ps1`. No admin needed.

## Credits

Icons: [Font Awesome Free](https://fontawesome.com) 7.3.1 by Fonticons, Inc., licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (see FontAwesome-LICENSE.txt).