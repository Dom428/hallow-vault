# Hallow Vault roadmap

## Done

| Version | What it added |
|---|---|
| **1.0** | Finds saves (Steam cloud folders for every account, Unity and Unreal games, a list of known games, your own folders); backs up when a game closes and once a day; skips unchanged saves; keeps the newest versions; restores any version after a safety backup; Hallow apps' settings (never webhooks or keys) |
| **1.1** | Delta Force's saves (kept inside the game folder) found; Hallow family link: pipe `HallowFamily.Vault.<user>` (ping, status, backup), `--cmd`, `status.json`; What's new after an update |

## Next

### 1.2 - Safer restores
- See what changed between two versions (files added, removed, bigger or smaller) before restoring
- Back up right before a game updates (Steam shows the update first), since updates are when saves break
- Never back up while the game is writing (wait for the files to settle)

### 1.3 - Off this PC
- Second copy to another folder: a OneDrive, Google Drive or Dropbox folder, or a USB drive, picked by you (no accounts inside Vault)
- Size limit per game and for the whole vault, oldest versions go first

### 1.4 - Hallow family
- Hallow Hub's game page shows Saves (last backup, Back up now, Restore)
- Hallow Remote: Back up now from the phone

### Later
- Fixes from real use: check the game-closed backup after real game sessions
- Pin a version so it's never pruned (before a big boss, a finished run)
