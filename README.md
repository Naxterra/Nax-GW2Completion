# Nax Completion (Blish HUD module)

Hands-off map completion for Guild Wars 2. One small panel with an arrow points at the nearest thing you have not done yet, and points tick themselves off as you play. Inside a jumping puzzle, and on the climb to a hard vista, it draws the next few spots of the route into the world, each marked as walk, run, jump or fly.

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Needs the .NET SDK (8 or newer). The result is `dist\NaxCompletion.bhm`. Close Blish HUD from the tray, copy that file into Blish HUD's `modules` folder, start Blish HUD and enable "Nax Completion". For jumping puzzle achievements, Blish HUD needs a GW2 API key with the "account" and "progression" permissions (added under Blish HUD's API settings), and the module must be allowed to use them once: in Blish HUD's module settings, disable Nax Completion, open its API permissions, untick and tick them, and enable the module again.

Tests: `bash dev/run-tests.sh <folder outside the project>` compiles and runs the harness.

## What ticks itself

| Kind | How |
|---|---|
| Waypoints, points of interest | When you get within the auto-tick radius (default 15 m) |
| Hearts, vistas, hero challenges, waypoints, points of interest | Read from the compass (minimap) every couple of seconds while you play, and from the world map whenever you have it open and still, for every Tyria map in view |
| Jumping puzzles | At the end of the route you followed, and from your account's jumping puzzle achievements |

Reading icons never unticks anything. Hearts are classified from their gold fill, which grows from the bottom up while you work on them. Nearly complete hearts can still be misclassified, so use undo if a heart ticks early. An icon under your own marker on the compass cannot be read, so a heart you just finished ticks a few metres later. A rotating compass is not read. The game draws no icons inside unexplored fog, so points there tick once you have uncovered the area.

The GW2 API does not report which waypoints, points of interest, vistas or hearts a character has done, so a new progress file (a new computer, for example) is filled from the game's own icons. Open the world map, zoom in until the icons show, and pan slowly across the maps you have already explored. Every still view ticks what the game shows as done, on the map you are on and on its neighbours. A settled view is refreshed every two seconds, including after moving a tooltip away or a failed capture. The outlines of the other maps come from `data\map_index.json`, which lists the public Tyria maps. A map missing from it is still read while you are on it.

## The panel

The arrow points at the nearest open point. It switches to another point only when that one is clearly closer (at most 70% as far and at least 25 m closer), and it holds a vista, heart or hero challenge while you are close to it. Arrow colour: green within 50 m, yellow within 200 m, orange further.

| Key | Does |
|---|---|
| Ctrl+Shift+U | Show only one kind: all, waypoints, POIs, vistas, hearts, hero challenges, jumping puzzles |
| Ctrl+Shift+L | Skip to the next nearest point |
| Ctrl+Shift+K | Tick the point the arrow points at, if the automation missed something |
| Ctrl+Shift+J | Undo the last tick on this map, within two minutes |

Mark whole map done and Reset this map have no key by default. Hold Ctrl and drag the panel to move it anywhere; with "Lock panel position" unticked it moves without Ctrl.

## Vistas

Vistas that need a climb come with an approach route: 168 vistas in core Tyria, cut from the barefoot (no mount) map completion route of Lady Elyssa's Guides (github.com/LadyElyssa/LadyElyssaTacoTrails), used with credit as the author permits. Within 60 m of such a vista the panel and the rings in the world lead you up step by step, like a jumping puzzle. At the vista the panel says "View the vista here". View it, and its map icon then ticks it. Vistas you can simply walk up to have no route, the arrow is enough there.

## Jumping puzzles

The module knows 68 jumping puzzles, and 58 of them come with a route through the whole puzzle. Those routes are converted from the jumping puzzle trails of Lady Elyssa's Guides (github.com/LadyElyssa/LadyElyssaTacoTrails), used with credit as the author permits. The arrow leads you to the start of the route, or to the nearest waypoint for the puzzles without one.

You can also record your own route, for example a shortcut, and it replaces the shipped one for that puzzle. Stand at the start and press Ctrl+Shift+P, then press it again at every spot worth remembering. The module notes by itself whether you walked, made a running jump, jumped from standing, or glided or rode a mount to get there. Ctrl+Shift+U changes that for the last spot if the guess is wrong, Ctrl+Shift+N adds a short note to it, Ctrl+Shift+J removes it, Ctrl+Shift+L picks another puzzle when the wrong one was chosen, and Ctrl+Shift+O saves.

Following a route: walk into the puzzle. The next three spots appear in the world as thin rings lying around the spot to reach, joined by a thin dashed line that stops short of both ends, so the edge you jump from and the spot you land on stay visible. The next spot is drawn brightest. White means walk, gold run, orange jump and light blue fly, and the panel shows the next step with distance, height difference and the note (for example "Glide from here"). After a fall it leads you back to the nearest spot you can reach. Reaching the end after following the route (at least 40% of its steps on that visit) ticks the puzzle by itself. Puzzles you finished at any time, also on another character, tick themselves from your account's jumping puzzle achievements (40 of the 68 puzzles have one; the module needs the account and progression permissions). Ctrl+Shift+K still ticks a puzzle by hand. A ticked puzzle is no longer guided. Everything disappears when you are more than 40 m from the route. Nothing is drawn outside a puzzle.

## Settings

Everything else is in the module settings: which kinds to track, the auto-tick radius, recognition on or off, and the optional frames on the world map (off by default; with them on, holding Alt and clicking a frame switches a point by hand). Turning off puzzle tracking stops its guidance and automatic completion. Hiding the arrow panel hides the route drawing too, while completion from following a tracked route continues. Disconnected route sections retain their notes and target rings, with no dashed line across the transition; gaps longer than 30 m also have no connecting line.

Recognition reads the game window independently of the optional overlay frames. Small frames and the overlay's zoom hint do not hide clear game icons from recognition. Objectives whose icon searches overlap remain excluded to avoid giving two points credit from one icon.

## Limits

- The game API does not say what a character has explored. Hero challenges are read from icons because the API endpoint has been unreliable. Compass recognition covers the map you are on; the world map also reads visible neighbouring maps in the bundled index. Covered icons, icons near the cursor or the compass centre, and crowded icons are skipped until they are readable.
- Distance is flat (no height), so a point on a cliff above you can auto-tick. Undo fixes it, and the point is not re-ticked until you walk away and come back.
- Progress files: Blish HUD data folder, `nax-completion\<character>.json`. Puzzle routes: `nax-completion\routes\`, shared by all characters.

## License

The source is available under the [Nax Source-Available License 1.0](LICENSE). Third-party route data and other components retain their authors' rights and the credits above.
