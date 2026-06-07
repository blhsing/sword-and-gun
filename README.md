# Sword & Gun (刀劍與槍)

A 3D first-person arena shooter that ships as two parallel builds of the same game:

- **Web edition — `game.html`** — a single self-contained file running on Three.js in any browser. No build step; open and play.
- **Desktop edition — `Program.cs`** — a native port on **.NET 9 + raylib** (Raylib-cs). Same gameplay with a native window, borderless full-screen, local split-screen, and LAN over UDP.

Both share the design: six weapons, a drivable tank, climbable multi-tier buildings, health and weapon pickups, AI waves, and 1v1 duels.

| | Web (`game.html`) | Desktop (`Program.cs`) |
|---|---|---|
| Runtime | Browser + Three.js (CDN) | .NET 9 + raylib |
| Single-player waves | ✅ | ✅ |
| Local split-screen 2P | — | ✅ (two pointing devices) |
| LAN duel | WebRTC manual pairing | UDP broadcast discovery |
| Drivable tank | friendly tank | one per player (2P) / player + AI (single) |
| Renderer | WebGL, Three.js Lambert + fog | raylib + directional "sun" shader, 4× MSAA |

---

# Desktop edition (`Program.cs`)

## Build & run

Requires the **.NET 9 SDK**.

```
dotnet run            # from the repo folder
```

The window opens in **borderless full-screen** (toggle with `F11`). The first screen asks for a name and a mode.

## Modes

- **單人波次 — Single** — eight escalating waves of AI (shooters, brawlers, AI-driven tanks). You also get your own drivable tank.
- **同機雙人分割對戰 — Local split-screen** — 1v1 on one PC using the shared keyboard and **two pointing devices**. You enter both names and choose the win condition on the setup screen.
- **建立 / 搜尋區網房間 — LAN host / join** — 1v1 over the local network with automatic UDP-broadcast discovery; full bidirectional state sync (players, tanks, hits).

## Controls

| Action | Player 1 / Single / LAN | Player 2 (split-screen) |
|---|---|---|
| Move | `W` `A` `S` `D` | Arrow keys |
| Sprint | `Left Shift` | `Right Shift` |
| Jump | `Space` | `Right Ctrl` |
| Aim | Mouse / pointing device 1 | Pointing device 2 |
| Fire | Left mouse / tap | Device-2 click / tap |
| Aim / zoom (sniper scope) | Right mouse | Device-2 right button |
| Select weapon | `1`–`6` | `7` `8` `9` `0` `-` `=` |
| Cycle weapon | `Q` | `End` |
| Reload | `R` | `Backspace` |
| Enter / exit tank | `F` | `Right Alt` |
| Pause | `P` or `Esc` | — |
| Restart match | `F2` | |
| Main menu | `M` | |
| Toggle language | `F10` (anytime) | |
| Quit | `Ctrl+Q` (or `Esc` at the menu) | |

Player 2's weapon keys avoid the numpad so the scheme works on laptops. The in-game HUD shows each player's bindings.

## Language

The whole UI is **bilingual — 繁體中文 (default) / English**. Toggle with **F10** anytime (or the button on the web build); the choice is **persisted** across sessions (`%AppData%\SwordAndGun.lang` on desktop, `localStorage` on the web).

## Ranking

The start screen shows a persisted **top-10 high-score board** from single-player runs (name, score, and the wave reached or 勝利/Victory). Scores are saved to `%AppData%\SwordAndGun.scores` (desktop) / `localStorage` (web).

## Two-player setup, names & win conditions

Choosing split-screen opens a setup screen:

- **Names** — enter a name for P1 and P2 (`Tab` switches fields, `Ctrl+V` pastes). Each player's name is shown in their HUD and floats above their avatar in the world. P1 is **blue**, P2 is **orange**.
- **Win condition** (`←/→` to switch, `↑/↓` to adjust):
  - **先到 N 殺** — first to *N* kills (1–50, default 10), or
  - **限時 M 分鐘** — timed match (0.5–15 min); most kills when the clock hits zero wins. A live countdown shows at the top; ties are declared 平手.

## Pointing devices & laptop touchpads

Split-screen needs two independent pointers. The desktop build uses Windows **Raw Input** to tell devices apart:

- Any **external mouse** is its own controller.
- A **precision touchpad** is also supported — its HID digitizer reports are parsed into relative aim, and a physical click-pad press or a quick tap acts as the fire/assign button.
- The setup screen shows a **live device list** (`#index [mouse/touchpad] move / clicks`). Wiggle each device to confirm they register separately, then click/tap to assign P1 then P2.

## Weapons, tank, pickups

You start with **only the pistol**; the other five weapons are picked up from crates scattered around the map (see the shared sections below). Each player has a **drivable tank** (600 HP) parked near spawn — press the interact key within 5 m to get in. It fires a high-explosive shell, can smash destructible cover by ramming, and ejects you (−25 HP) if destroyed while you're inside. In single-player the AI also fields tanks.

## HUD & feedback

- A **weapon bar** shows all six slots: owned weapons bright, not-yet-found dimmed, **out-of-ammo in red**, the equipped one boxed; owned firearms also show their **current ammo**.
- **Player and tank HP bars** (coloured, value overlaid; tank bars also float above each tank in the world).
- **Damage-direction arrows** point toward incoming fire; **enemy HP bars** float over each enemy in single-player.
- Avatars are built from legs/torso/head and **visibly hold the current weapon**; gunfire shows a **muzzle flash**, melee shows a **sword swing**, and a first-person view-model is drawn for the local player.

## Graphics

- A fixed directional **sun**: every face of every box is shaded by its normal, so the six sides differ in tone.
- **4× MSAA** smooths edges and the ground grid.
- Borderless full-screen at the monitor's resolution; split-screen renders each half to its own render target so the crosshair and the shot direction line up exactly.

---

# Web edition (`game.html`)

A single-file build that runs entirely in the browser. No installation — open `game.html` and play.

## Quick start

1. Download `game.html`.
2. Double-click to open it in your browser.
3. Enter a player name, then click the screen — pointer lock engages and the round begins.

Internet is required on first load to fetch Three.js from the CDN; afterwards the browser cache serves it offline.

## LAN duel (WebRTC)

The browser version pairs two computers through **WebRTC manual signalling** (no STUN/TURN; same-LAN only):

1. Host clicks **建立區網房間** and sends the generated pairing code to the other computer.
2. The joiner pastes it, clicks **加入房間**, and sends its reply code back.
3. The host pastes the reply and clicks **套用對方回覆碼**.
4. Once connected, enter names and start — a 1v1 duel, first to 10 kills.

Browsers can't do raw UDP broadcast, so automatic IP discovery isn't available in the web edition (the desktop edition does it over UDP).

## Controls

| Action | Key / Input |
|---|---|
| Move | `W` `A` `S` `D` |
| Sprint | `Shift` |
| Aim | Mouse |
| Attack | Left mouse button |
| Aim / zoom (sniper scope) | Right mouse button |
| Switch weapon | `1`–`6` |
| Cycle weapon | `Q` |
| Reload | `R` |
| Enter / exit friendly tank | `F` (within 5 m) |
| Pause | `P` or `Esc`; click to resume |

The game truly pauses when pointer lock is released — enemies stop, bullets freeze, cooldowns hold. Input state resets on resume so a held key doesn't carry over.

## Tech

- **Three.js** via an import map from a CDN; pure ES module + vanilla DOM, no bundler.
- **Pointer Lock API** for mouse capture; **WebGLRenderer** with `antialias: true`.
- Local **top-10 ranking board** via `localStorage`.
- Every model is a `BoxGeometry`/`SphereGeometry` composition — the whole game is one ~40 KB HTML file.

---

# Shared design

## Weapons

You start with the pistol; the rest are found in crates around the arena.

| # | Name | Mag | Damage | Cooldown | Range | Notes |
|---|---|---:|---:|---:|---:|---|
| 1 | 手槍 Pistol | 16 | 24 | 0.22 s | 75 | Accurate, semi-auto |
| 2 | 衝鋒槍 SMG | 40 | 13 | 0.055 s | 58 | Full-auto, some spread |
| 3 | 機關槍 Machine Gun | 90 | 18 | 0.085 s | 85 | Full-auto |
| 4 | 火箭筒 RPG | 5 | 115 | 0.9 s | 100 | Projectile, ~7.5 m blast |
| 5 | 刀劍 Sword | — | 280 | 0.35 s | 4 | Melee, one-shots most |
| 6 | 狙擊槍 Sniper | 5 | 180 | 1.0 s | 220 | Long range, high damage |

## Enemies (single-player)

- **Shooter** (bright red) — keeps its distance, strafes, fires bolts.
- **Brawler** (orange) — charges into melee.
- **Tank** (crimson, ~400+ HP) — large, slow, fires heavy shells; explodes on death.

Enemies are vividly coloured, carry a HP bar above them, and **steer around obstacles** (probing a fan of headings) rather than grinding into cover. A `第 N 波` banner announces each wave; cleared waves trigger a 3-second countdown before the next. Per wave `n`: shooters `3 + n`, brawlers `n`, tanks `⌊(n−1)/2⌋`. Clearing wave 8 wins the run.

## Respawns

In the 2-player modes (desktop split-screen and both editions' LAN duel) players **respawn at random, obstacle-free positions** rather than fixed corners. Single-player has no respawn — death ends the run.

## Pickups

- **Health packs** — white-and-green crosses; walk over one to regain **55 HP** (cap 180), or **+120 tank HP** if you're driving a damaged tank. Respawns after ~18 s.
- **Weapon crates** — gold boxes that grant the weapon and a full magazine (and auto-equip it the first time). Respawn after ~22 s. Ammo is per-magazine: when a mag runs dry you must **reload (`R`)** — there's no auto-reload.

## Arena

A large square arena bounded by solid walls, with procedurally placed cover each round: tall pillars, low cover walls, and **climbable multi-tier buildings** (step up the stairs, jump between tiers). Spawn points are always cleared of cover. Destructible cover can be smashed by tanks.

## License

MIT.
