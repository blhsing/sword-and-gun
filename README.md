# Sword & Gun — Web Battlefield

A single-file 3D first-person arena shooter that runs entirely in the browser. No build step, no installation — open `game.html` and play.

Five weapons (pistol, SMG, machine gun, RPG, sword), three enemy types (shooters, brawlers, tanks), an AI ally that fights alongside you, a friendly tank you can drive, health pickups, and eight waves of escalating difficulty.

## Quick start

1. Download `game.html`
2. Double-click to open in your browser (or drag it into a browser window)
3. Click the screen — pointer lock engages and the round begins

Internet is required on first load to fetch Three.js from the CDN; after that the browser cache will serve it offline.

## Controls

| Action | Key / Input |
|---|---|
| Move | `W` `A` `S` `D` |
| Sprint | `Shift` |
| Aim | Mouse |
| Attack | Left mouse button |
| Switch weapon | `1` Pistol &nbsp; `2` SMG &nbsp; `3` Machine Gun &nbsp; `4` RPG &nbsp; `5` Sword |
| Cycle weapon | `Q` |
| Reload | `R` |
| Enter / exit friendly tank | `F` (within 5m of the green tank) |
| Pause | `P` or `Esc` |
| Resume | Click the canvas |

While driving the tank: `W`/`S` drive forward/back, `A`/`D` turn, mouse aims the turret, left click fires a high-explosive shell with ~10m blast radius.

## Weapons

| # | Name | Magazine | Damage | Fire rate | Notes |
|---|---|---:|---:|---|---|
| 1 | Pistol | 16 | 24 | 0.22s | Accurate, semi-auto |
| 2 | SMG | 40 | 13 | 0.055s | Full-auto, some spread |
| 3 | Machine Gun | 90 | 18 | 0.085s | Full-auto, orange tracer |
| 4 | RPG | 5 | 115 | 0.9s | Projectile, 7.5m AoE |
| 5 | Sword | — | 280 | 0.35s | 4m melee, one-shots most |

## Enemies

- **Shooter** (red, ~70 HP) — keeps distance 7-18m, strafes, fires projectiles at 12 damage.
- **Brawler** (orange, ~95 HP) — charges into melee range and hits for 18.
- **Tank** (dark red, ~420 HP) — large, slow, fires heavy shells (38 damage, 0.22m radius). Drops a self-explosion on death.

Each wave HP scales with wave number. Bigger waves include more of each type.

## Allies

- **One friendly trooper** (blue, 170 HP). Follows the player at ~4m offset, auto-targets nearest enemy within 45m, fires every 0.28s for 8 damage. Respawns 8 seconds after dying.
- **One friendly tank** (green, 600 HP). Sits near spawn — walk to it and press `F` to enter. While inside, you control it directly and the camera attaches to the turret. Take heavy fire without losing player HP, but the tank can be destroyed; if it explodes while you're inside, you're ejected with 25 damage. After destruction it does not respawn for the round.

## Pickups

- **Health packs** — 12 white-and-green crosses placed around the map. Walk over one (within 2m) and you regain 55 HP up to the cap of 180. Each pack respawns 18 seconds after pickup.

## Waves

Eight waves total. Composition by wave `n`:

- Shooters: `4 + 2n`
- Brawlers: `2 + n`
- Tanks: `1 + ⌊n / 2⌋`

A `WAVE n` banner appears at the start of each wave. After clearing, you get a `WAVE n CLEAR!` banner and a 2.5-second breather before the next wave spawns. Clearing wave 8 wins the run.

## Arena

156×156 meter square arena, bounded by 4m walls. Procedurally placed terrain each load:

- 46 mixed pillars (random size 2-7m wide, 1.2-5m tall)
- 14 low cover walls (~9-17m long, 1.1m tall)
- Central open plaza and two corner platforms as landmarks

A central safe zone keeps obstacles away from the spawn point.

## Tech

- **Three.js r160** via `<script type="importmap">` from jsdelivr CDN.
- **Pure ES module + vanilla DOM**. No bundler, no transpile, no `package.json`.
- **Pointer Lock API** for FPS mouse capture; `Esc`/`P` release lock, click to re-engage.
- **WebGLRenderer** with `antialias: true`. Pixel ratio capped at 2 for high-DPI displays.
- **Axis-separated AABB collision** for player and enemies versus static obstacles.
- **Hitscan** for bullets (ray-vs-AABB), explicit projectiles only for RPG, tank shells, and enemy fire.
- **Lambert lighting** with a hemisphere + directional pair. Fog at 35-140m for atmosphere.
- **No external assets** — every model is a `BoxGeometry`/`SphereGeometry` composition. The whole game ships in a single ~42 KB HTML file.

## Pausing

The game truly pauses when pointer lock is released — enemies stop, bullets freeze, cooldowns hold. Press `P` to release the mouse, click the canvas to dive back in. Input state is also reset on resume so a key held while pausing doesn't trigger movement on the first frame back.

## License

MIT.
