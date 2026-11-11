# ShreddedBots 0.8

**AI teammates for Teenage Mutant Ninja Turtles: Shredder's Revenge (offline local play)**

Copyright © 2026 MrRobotoDomoArigato · [YouTube](https://www.youtube.com/@MrRobotoDomoArigato)

For feedback, feel free to reach out to report issues or features you would like to see.


## Important – Please Read

ShreddedBots is an unofficial fan mod. It is not affiliated with, endorsed by, or
supported by Tribute Games, Dotemu, Paramount, Nickelodeon, or Valve. You need your own
legitimate copy of TMNT: Shredder's Revenge on Steam. This package contains no game code
and no game assets - only the mod loader and its redistributable dependency (Harmony).


## What It Does

Adds computer-controlled teammates ("bots") for players P2-P6 in offline local play so you
can run a multi-player session on your own. The bots fight, follow you, revive downed
players, throw enemies, pick up leftover health pizza when it is safe, team up for sandwich
attacks and high-fives, and use smarter behaviour in Survival and against major bosses
(Shredder, Super Shredder, Statue of Tyranny, Tenderizer, and related fights).


## Requirements

- Steam copy of Teenage Mutant Ninja Turtles: Shredder's Revenge
- Steam client running and logged in
- Windows with .NET Framework 4.x (4.5.2 or newer; already on Windows 10/11)


## Install Steam

1. Close the game.
2. In Steam, right-click TMNT: Shredder's Revenge > Manage > Browse local files.
   That folder contains TMNT.exe.
3. Copy these files into that folder (next to TMNT.exe):
   - `ShreddedBots.exe`
   - `ShreddedBots.exe.config`
   - `0Harmony.dll`
4. Make sure Steam is open and you are logged in.
5. Start the game by double-clicking ShreddedBots.exe (instead of pressing Play in Steam).
   ShreddedBots starts the normal game for you.

Tip: right-click ShreddedBots.exe > Send to > Desktop (create shortcut).

Note: ShreddedBots opens a small console window next to the game. If you click inside it,
Windows may pause the game ("Select" appears in its title). Press Esc in that window to
continue.


## Uninstall

Close the game and delete ShreddedBots.exe, ShreddedBots.exe.config, 0Harmony.dll, and
optionally ShreddedBots.ini, ShreddedBots.log, and ShreddedBots.prev.log. Nothing else was
changed. Starting the game from Steam as usual always gives you the unmodded game.


## Using The Bots – The F7 Menu

Press F7 (keyboard) at any time to open or close the ShreddedBots menu. While it is open
the game ignores the keyboard so you will not move by accident.

Bots start off. Press F7 and switch on the P2-P6 Bots you want.

```
Up / Down              Move through the menu
Left / Right           Change a value
Enter / Space          Switch an item on or off
Esc / Backspace / F7   Close the menu
```

Menu items:

**Bot skill: Chill / Okay / Gnarly (default Gnarly)**  
How sharp the bots play. Gnarly is the full behaviour. Okay reacts a little slower and
uses flashy moves less often. Chill sticks to simpler combos and reacts later. On every
skill they still revive, cross gaps, fight bosses, and use team moves.

**Aggression: Low / Normal / High (default Normal)**  
How far they engage and how eager they are to charge in. High pushes them a bit
farther and sooner; Low holds them back a little.

**P2 Bot ... P6 Bot: ON / OFF**  
Each switch controls its own player slot (P5 Bot always plays as P5, and so on).
Slots that are switched off stay open, so real players can still join there.
A bot never takes a slot a player is already using. All off = no bots.
Every bot is OFF by default.

**P2 Bot ... P6 Bot: (character)**  
Which turtle or ally that slot plays (or Auto to pick a free character).

**P2 Bot ... P6 Bot color: First (default) / Color N**  
Costume color for that bot. On the character select screen it updates live; mid-run
the color applies the next time that bot joins.

**Close**  
Closes the menu.

Your choices are saved automatically in ShreddedBots.ini in the game folder.


## Features

- Up to five bots on P2-P6, each with On/Off, character, and color
- Revives of downed players when it is safe enough to try
- Sandwich attacks (two fighters on opposite sides of an enemy) with bots and with you
- High-fives between bots (and toward you) for a small heal when the fight is quiet
- Throws chosen from the situation (not into pits; safer tosses when crowded)
- Health pizza pickup: a hurt bot may grab leftover pizza near you; they will not run
  across the map or leave you behind for it
- Survival smarts (portals, careful revives, ranged focus) and boss smarts (Shredder,
  Super Shredder, Statue of Tyranny, Tenderizer, and related fights)
- Pit and gap handling so bots jump what they can clear or wait at the edge instead of
  walking into holes


## Safety Notes

- Offline / local play only. Online play is blocked while you launch through ShreddedBots.
- Crash reporting and Discord / analytics reporting from the game are switched off for
  that launch. The mod does not send anything anywhere.
- ShreddedBots does not modify any game file. It starts the game and patches it in memory
  only while it runs.
- Your saves are not touched.
- There is no cheat menu - only the bot options above.


## Troubleshooting

**"TMNT.exe was not found"**  
The mod files must sit next to TMNT.exe. Use Steam > Browse local files.

**Game looks frozen while starting**  
Click the ShreddedBots console window and press Esc (see the note under Install).

**Steam / odd behaviour**  
Keep the Steam client running and logged into the account that owns the game, then
start ShreddedBots.exe again. Check ShreddedBots.log.

**Windows SmartScreen or antivirus**  
ShreddedBots.exe is not code-signed and uses Harmony to patch the game in memory, so
some tools flag it. Only download it from the official release page.

**.NET Framework error**  
Install or repair Microsoft .NET Framework 4.x from Microsoft.

**Game updated and the mod breaks**  
Play from Steam (unmodded) until a ShreddedBots update. Note your game build.

**Settings seem broken**  
Close the game and delete ShreddedBots.ini; defaults are recreated next launch.

When asking for help, send ShreddedBots.log (and ShreddedBots.prev.log if present), what
you were doing, and your Windows version. Logs may contain your Windows user name in paths.


## Building

See [BUILDING.md](BUILDING.md) for how to build `ShreddedBots.exe` from this source with your own copy of the game.

## Credits

ShreddedBots by MrRobotoDomoArigato
[YouTube](https://www.youtube.com/@MrRobotoDomoArigato)


## Disclaimer / License

Unofficial fan project. Not affiliated with Tribute Games, Dotemu, Paramount, Nickelodeon,
or Valve. No game files are included. Provided free of charge, AS IS, with no warranty.
Use at your own risk.

Teenage Mutant Ninja Turtles: Shredder's Revenge and related names and assets belong to
their respective owners.

The mod source is MIT licensed (see [LICENSE](LICENSE)). It uses the Harmony library (MIT license) - see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
