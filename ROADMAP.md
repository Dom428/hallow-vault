# Hallow Vault roadmap

## Done

| Version | What it added |
|---|---|
| **1.0** | Finds saves (Steam cloud folders for every account, Unity and Unreal games, a list of known games, your own folders); backs up when a game closes and once a day; skips unchanged saves; keeps the newest versions; restores any version after a safety backup; Hallow apps' settings (never webhooks or keys) |
| **1.1** | Delta Force's saves (kept inside the game folder) found; Hallow family link: pipe `HallowFamily.Vault.<user>` (ping, status, backup), `--cmd`, `status.json`; What's new after an update |
| **1.2** | Compare two versions (or a version and your saves now) before restoring; back up when Steam's app manifest shows an update waiting or starting; wait until no save file changed for 10 s |
| **1.3** | Second copy to a folder you pick (cloud-synced folder or USB drive), mirrored after each backup and pruned in step; size limits per game and for the whole vault (oldest first, never a game's newest) |

## Next

### 1.4 - Hallow family
- Hallow Hub's game page shows Saves (last backup, Back up now, Restore)
- Hallow Remote: Back up now from the phone

### Later
- Fixes from real use: check the game-closed backup after real game sessions
- Pin a version so it's never pruned (before a big boss, a finished run)
