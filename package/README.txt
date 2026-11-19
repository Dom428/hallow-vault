HALLOW VAULT - backs up your game saves and settings, and puts any version back

HOW TO INSTALL
1. Right-click the zip > Properties > tick "Unblock" > OK
2. Right-click the zip > Extract All
3. Double-click INSTALL.bat (no admin needed)
4. Hallow Vault opens. It's in your Start menu and on your Desktop.

WHAT'S NEW IN 1.10
- The second copy shows whether it's up to date, behind or out of reach, and catches up by itself.
- Spots sync conflict copies your cloud app made in the second copy.

WHAT'S NEW IN 1.9
- Settings: also back up every few hours, compression, and the Backup log.
- Right-click a game > Files to back up: include or leave out files (like *.sav or Screenshots\*).

WHAT'S NEW IN 1.8
- Updates itself from GitHub - it asks first (Settings > Check for updates).
- Settings: back up every few hours, compression, and the Backup log. Right-click a game to pick
  which files to back up.
- The second copy shows whether it's up to date or behind, and catches up by itself.
- Games that keep settings in the registry (many Unity games) are backed up too, and more save
  folders are found for games Vault didn't know.

WHAT'S NEW IN 1.7
- Pin a version (Restore > pin) so it's never pruned.
- Save alarm: saves that suddenly shrink or vanish aren't backed up over - restore the last good one.
- New PC: Settings > Export, then Import on the new PC puts every game's newest saves back.

WHAT'S NEW IN 1.6
- Uses the Hallow accent colour you pick in Hallow Toolkit (Settings to turn it off).
- If Vault ever closes unexpectedly it keeps a crash report on this PC and tells you next time.

WHAT'S NEW IN 1.5
- One Hallow icon: with Hallow Hub running, Vault lives in Hub's tray menu (Back up everything,
  Restore, Open the vault folder) instead of its own icon. Settings > Use the Hallow icon.

WHAT'S NEW IN 1.4
- Hallow Hub's game pages show each game's Saves: last backup, Back up now, Restore.
- Hallow Remote: Back up now from your phone.

WHAT'S NEW IN 1.3
- Second copy (Settings): every backup is copied to a OneDrive, Google Drive or Dropbox folder, or a
  USB drive, and old ones are removed in step.
- Size limits per game and for the whole vault - the oldest versions go first, never a game's newest.

WHAT'S NEW IN 1.2
- Restore > Compare: see which files were added, removed, got bigger or smaller before you restore.
- Backs up a Steam game right before it updates (Settings), and never while a game is still writing
  its saves (it waits until they've been quiet for 10 seconds).

WHAT'S NEW IN 1.1
- Fixed: Hallow Vault could close itself when it saved its settings (after a backup).
- Delta Force's settings and saves are found and backed up now.
- Hallow Hub and Hallow Remote can see your backups and start one (Hallow family link).
  For scripts and Stream Deck: HallowVault.exe --cmd "backup all" (or "backup Deadlock").

WHAT IT DOES
- Finds where your games keep saves and settings: Steam's own folders, Unity and Unreal
  games' save folders, and a few well-known places. Add any other folder yourself.
- Also backs up the Hallow apps' settings (Hallow Mixer, its EQ, Hallow Hub).
- Backs up a game when it closes (only if something changed), and everything once a day.
- Keeps the last 10 backups of each game (5 to 50 in Settings).
- Restore: pick any version and it's put back. Your current saves are backed up first.
  Compare shows which files were added, removed, got bigger or smaller first.
- Backups are ordinary .zip files. By default they go to a second drive if you have one -
  a network folder (like your server) is even safer: Settings > Change.
- Second copy (Settings): every backup is copied to a OneDrive / Google Drive / Dropbox folder or a
  USB drive too. Size limits per game and for the whole vault (oldest versions go first).
- Logs, caches, crash dumps and secrets (like app keys) are left out.
- Works with Hallow Hub (each game's Saves on its page) and Hallow Remote (Back up now from your phone).
- With Hallow Hub running, Vault lives in Hub's tray icon instead of its own (Settings > Use the Hallow icon).
- Uses the Hallow accent colour picked in Hallow Toolkit. Crash reports stay on this PC (never sent).
- Pin a version so it's never pruned. Saves that suddenly shrink or vanish aren't backed up over.
- New PC: Settings > Export on the old one, Import on the new one.
- Games that keep settings in the registry (many Unity games) are backed up too.
- Updates itself from GitHub - it asks first (Settings > Check for updates).
- Settings: back up every few hours, compression, and the Backup log. Right-click a game to pick
  which files to back up.
- The second copy shows whether it's up to date or behind, and catches up by itself.

UNINSTALL
Windows Settings > Apps > Hallow Vault > Uninstall. Your backups are never deleted.

CREDITS
Icons: Font Awesome Free 7.3.1 by Fonticons, Inc. (https://fontawesome.com), licensed CC BY 4.0 - see FontAwesome-LICENSE.txt.