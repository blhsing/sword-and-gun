using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raylib_cs;

// =========================================================================
// Constants
// =========================================================================
const float ArenaSize = 78f;
const float PlayerRadius = 0.45f;
const float PlayerMaxHp = 180f;
const float TankCollideR = 2.6f;
const float BaseFov = 75f;
const int SampleRate = 44100;
const int VersusKillsToWin = 10;
const float VersusRespawnDelay = 3f;
const float CoopReviveDelay = 5f;

// =========================================================================
// Window + audio init
// =========================================================================
Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
Raylib.InitWindow(1280, 720, "Sword & Gun - Battlefield");
Raylib.SetTargetFPS(60);
Raylib.SetExitKey(KeyboardKey.Null); // we handle ESC ourselves
Raylib.SetMouseCursor(MouseCursor.Default);

int monitor = Raylib.GetCurrentMonitor();
int monW = Raylib.GetMonitorWidth(monitor);
int monH = Raylib.GetMonitorHeight(monitor);
if (monW > 0 && monH > 0) Raylib.SetWindowSize(monW, monH);
Raylib.ToggleBorderlessWindowed();

int Width = Raylib.GetScreenWidth();
int Height = Raylib.GetScreenHeight();

Raylib.InitAudioDevice();
var sfx = new SfxBank(SampleRate);

var rng = new Random();

// =========================================================================
// Weapons
// =========================================================================
var weapons = new Weapon[]
{
    new("Pistol",      16, 24f,  0.22f,  1.0f,  75f,  false, 0.012f, false, false, false, "pistol"),
    new("SMG",         40, 13f,  0.055f, 1.45f, 58f,  false, 0.035f, true,  false, false, "smg"),
    new("Machine Gun", 90, 18f,  0.085f, 2.2f,  85f,  false, 0.028f, true,  false, false, "mg"),
    new("RPG",         5,  115f, 0.9f,   2.5f,  100f, true,  0f,     false, false, false, "rpg"),
    new("Sword",       0,  280f, 0.35f,  0f,    4.0f, false, 0f,     false, true,  false, "sword"),
    new("Sniper",      5,  180f, 1.0f,   2.2f,  220f, false, 0f,     false, false, true,  "sniper"),
};

// =========================================================================
// Key bindings (load from disk)
// =========================================================================
string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwordAndGun");
string configPath = Path.Combine(configDir, "keybindings.json");
KeyBindingsConfig bindings = LoadBindings();

// =========================================================================
// World state
// =========================================================================
var obstacles = new List<BoundingBox>();
var platforms = new List<TerrainBlock>();
var enemies = new List<Enemy>();
var allies = new List<Ally>();
var healthPacks = new List<HealthPack>();
var enemyBullets = new List<Bullet>();
var playerRockets = new List<Rocket>();
var tracers = new List<Tracer>();
var hitMarks = new List<HitMark>();
var explosions = new List<Explosion>();
var weaponPickups = new List<WeaponPickup>();
var friendlyTanks = new List<FriendlyTank>();

// =========================================================================
// Per-player state
// =========================================================================
int playerCount = 1;
var players = new Player[]
{
    new Player(0, weapons, bindings.P1),
    new Player(1, weapons, bindings.P2),
};

int waveNum = 1;
bool gameOver = false;
bool won = false;
float waveDelay = 0f;
bool waveInProgress = false;
float bannerTimer = 0f;
string bannerText = "";

GameMode gameMode = GameMode.CoOp;
SplitMode splitMode = SplitMode.Horizontal;
bool weaponPickupsMode = false;
float versusTimer = 300f; // 5 minutes versus round

// =========================================================================
// State machine
// =========================================================================
AppState appState = AppState.StartMenu;
int menuCursor = 0;
bool prevP = false;
bool prevEnter = false;
bool prevEsc = false;

// Key rebind state
int rebindActionIdx = -1;
int rebindPlayer = 0;
string[] _actionNames = { "Forward","Back","Left","Right","Sprint","Reload","Cycle Weapon","Enter Tank","Weapon 1","Weapon 2","Weapon 3","Weapon 4","Weapon 5","Weapon 6" };

// Mouse identification (2P)
int identifyStage = 0; // 0=waiting P1, 1=waiting P2, 2=done

// =========================================================================
// Raw Input
// =========================================================================
bool rawInputAvailable = false;
try
{
    IntPtr hwnd = GetWindowHandleSafe();
    if (hwnd != IntPtr.Zero) rawInputAvailable = RawMouse.Initialize(hwnd);
}
catch (Exception ex) { Console.WriteLine($"RawInput init failed: {ex.Message}"); }

// =========================================================================
// Render textures (allocated lazily; recreated on window resize / split change)
// =========================================================================
RenderTexture2D rt0 = default, rt1 = default;
int rtW0 = 0, rtH0 = 0, rtW1 = 0, rtH1 = 0;
bool rtsInited = false;

// =========================================================================
// Build initial world (single player by default)
// =========================================================================
BuildBattlefield();

// =========================================================================
// Main loop
// =========================================================================
while (!Raylib.WindowShouldClose())
{
    float dt = Raylib.GetFrameTime();
    Width = Raylib.GetScreenWidth();
    Height = Raylib.GetScreenHeight();
    if (Raylib.IsKeyPressed(KeyboardKey.F11)) Raylib.ToggleBorderlessWindowed();

    EnsureRenderTextures();

    bool enterNow = Raylib.IsKeyDown(KeyboardKey.Enter);
    bool escNow = Raylib.IsKeyDown(KeyboardKey.Escape);
    bool enterPressed = enterNow && !prevEnter;
    bool escPressed = escNow && !prevEsc;
    prevEnter = enterNow; prevEsc = escNow;

    switch (appState)
    {
        case AppState.StartMenu:
            HandleStartMenu(enterPressed, escPressed);
            DrawStartMenu();
            break;
        case AppState.ModeMenu:
            HandleModeMenu(enterPressed, escPressed);
            DrawModeMenu();
            break;
        case AppState.KeyConfigMenu:
            HandleKeyConfigMenu(enterPressed, escPressed);
            DrawKeyConfigMenu();
            break;
        case AppState.IdentifyMice:
            HandleIdentifyMice();
            DrawIdentifyMice();
            break;
        case AppState.Playing:
            HandlePlaying(dt);
            RenderSplit();
            break;
        case AppState.Paused:
            HandlePaused();
            RenderSplit();
            DrawPauseOverlay();
            break;
        case AppState.GameOver:
            HandleGameOver(enterPressed, escPressed);
            RenderSplit();
            DrawGameOverOverlay();
            break;
    }

    if (RawMouse.IsRegistered) RawMouse.NewFrame();
}

UnloadRenderTextures();
sfx.Unload();
Raylib.CloseAudioDevice();
Raylib.CloseWindow();
return;

// =========================================================================
// Setup / teardown helpers
// =========================================================================
IntPtr GetWindowHandleSafe()
{
    unsafe
    {
        void* h = Raylib.GetWindowHandle();
        return (IntPtr)h;
    }
}

KeyBindingsConfig LoadBindings()
{
    try
    {
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var cfg = JsonSerializer.Deserialize<KeyBindingsConfig>(json, JsonOptions.Default);
            if (cfg != null) return cfg;
        }
    }
    catch (Exception ex) { Console.WriteLine($"LoadBindings: {ex.Message}"); }
    return new KeyBindingsConfig();
}

void SaveBindings()
{
    try
    {
        Directory.CreateDirectory(configDir);
        var json = JsonSerializer.Serialize(bindings, JsonOptions.Default);
        File.WriteAllText(configPath, json);
    }
    catch (Exception ex) { Console.WriteLine($"SaveBindings: {ex.Message}"); }
}

void EnsureRenderTextures()
{
    int w0, h0, w1, h1;
    if (playerCount == 1)
    {
        w0 = Width; h0 = Height; w1 = 0; h1 = 0;
    }
    else if (splitMode == SplitMode.Horizontal)
    {
        w0 = Width / 2; h0 = Height;
        w1 = Width - w0; h1 = Height;
    }
    else
    {
        w0 = Width; h0 = Height / 2;
        w1 = Width; h1 = Height - h0;
    }

    if (rtsInited && w0 == rtW0 && h0 == rtH0 && w1 == rtW1 && h1 == rtH1) return;
    UnloadRenderTextures();
    if (w0 > 0 && h0 > 0) rt0 = Raylib.LoadRenderTexture(w0, h0);
    if (w1 > 0 && h1 > 0) rt1 = Raylib.LoadRenderTexture(w1, h1);
    rtW0 = w0; rtH0 = h0; rtW1 = w1; rtH1 = h1;
    rtsInited = true;
}

void UnloadRenderTextures()
{
    if (rtsInited)
    {
        if (rtW0 > 0 && rtH0 > 0) Raylib.UnloadRenderTexture(rt0);
        if (rtW1 > 0 && rtH1 > 0) Raylib.UnloadRenderTexture(rt1);
        rtsInited = false;
    }
}

// =========================================================================
// State: StartMenu
// =========================================================================
void HandleStartMenu(bool enterPressed, bool escPressed)
{
    if (Raylib.IsKeyPressed(KeyboardKey.One)) { StartSinglePlayer(); }
    if (Raylib.IsKeyPressed(KeyboardKey.Two)) { playerCount = 2; gameMode = GameMode.CoOp; appState = AppState.ModeMenu; menuCursor = 0; }
    if (Raylib.IsKeyPressed(KeyboardKey.Three)) { playerCount = 2; gameMode = GameMode.Versus; appState = AppState.ModeMenu; menuCursor = 0; }
    if (Raylib.IsKeyPressed(KeyboardKey.K)) { rebindPlayer = 0; rebindActionIdx = -1; appState = AppState.KeyConfigMenu; menuCursor = 0; }
    if (escPressed) Environment.Exit(0);
}

void StartSinglePlayer()
{
    playerCount = 1;
    gameMode = GameMode.CoOp;
    ResetWorld();
    appState = AppState.Playing;
}

void DrawStartMenu()
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color((byte)20, (byte)25, (byte)40, (byte)255));
    string title = "SWORD & GUN";
    int tw = Raylib.MeasureText(title, 80);
    Raylib.DrawText(title, Width / 2 - tw / 2, 80, 80, Color.RayWhite);
    string sub = "Choose mode";
    int sw = Raylib.MeasureText(sub, 32);
    Raylib.DrawText(sub, Width / 2 - sw / 2, 200, 32, Color.Yellow);
    int y = 280;
    int gap = 56;
    DrawMenuLine("[1]  Single Player", y, false); y += gap;
    DrawMenuLine("[2]  2 Players  -  Co-op  (vs enemies, 2 mice required)", y, false); y += gap;
    DrawMenuLine("[3]  2 Players  -  Versus  (player vs player, 2 mice required)", y, false); y += gap;
    DrawMenuLine("[K]  Key bindings", y, false); y += gap;
    DrawMenuLine("[ESC]  Quit", y, false); y += gap;

    string rawInfo = rawInputAvailable
        ? "Raw mouse input registered (2-mouse support available)"
        : "Raw mouse input UNAVAILABLE - 2-player mode will not work properly";
    Color rawCol = rawInputAvailable ? Color.Lime : Color.Orange;
    Raylib.DrawText(rawInfo, Width / 2 - Raylib.MeasureText(rawInfo, 20) / 2, Height - 60, 20, rawCol);
    Raylib.EndDrawing();
}

void DrawMenuLine(string text, int y, bool selected)
{
    int sz = 28;
    int tw = Raylib.MeasureText(text, sz);
    Color c = selected ? Color.Yellow : Color.White;
    Raylib.DrawText(text, Width / 2 - tw / 2, y, sz, c);
}

// =========================================================================
// State: ModeMenu (2P only)
// =========================================================================
void HandleModeMenu(bool enterPressed, bool escPressed)
{
    if (escPressed) { appState = AppState.StartMenu; return; }
    if (Raylib.IsKeyPressed(KeyboardKey.H)) splitMode = SplitMode.Horizontal;
    if (Raylib.IsKeyPressed(KeyboardKey.V)) splitMode = SplitMode.Vertical;
    if (Raylib.IsKeyPressed(KeyboardKey.W)) weaponPickupsMode = !weaponPickupsMode;
    if (enterPressed)
    {
        if (rawInputAvailable)
        {
            identifyStage = 0;
            RawMouse.Devices.Clear();
            players[0].MouseHandle = IntPtr.Zero;
            players[1].MouseHandle = IntPtr.Zero;
            appState = AppState.IdentifyMice;
        }
        else
        {
            // No raw input — fall back to single mouse controlling P1 only
            ResetWorld();
            appState = AppState.Playing;
        }
    }
}

void DrawModeMenu()
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color((byte)20, (byte)25, (byte)40, (byte)255));
    string title = gameMode == GameMode.CoOp ? "2P CO-OP SETUP" : "2P VERSUS SETUP";
    int tw = Raylib.MeasureText(title, 60);
    Raylib.DrawText(title, Width / 2 - tw / 2, 90, 60, Color.RayWhite);
    int y = 220;
    int gap = 56;
    string splitText = splitMode == SplitMode.Horizontal
        ? "[H/V]  Split:  Horizontal (left/right)  -  press V for vertical"
        : "[H/V]  Split:  Vertical (top/bottom)  -  press H for horizontal";
    DrawMenuLine(splitText, y, false); y += gap;
    string wpText = weaponPickupsMode
        ? "[W]    Weapon pickups: ON  - start with Pistol only, pick up others on map"
        : "[W]    Weapon pickups: OFF - start with all weapons unlocked";
    DrawMenuLine(wpText, y, false); y += gap;
    string mInfo = gameMode == GameMode.CoOp
        ? "Co-op: AI enemies present, both players fight them. Per-player kill score."
        : "Versus: NO enemies. PvP. First to " + VersusKillsToWin + " takedowns wins, 5-minute timer.";
    DrawMenuLine(mInfo, y, false); y += gap * 2;
    DrawMenuLine("[ENTER] Continue", y, false); y += gap;
    DrawMenuLine("[ESC]   Back", y, false);
    Raylib.EndDrawing();
}

// =========================================================================
// State: KeyConfigMenu
// =========================================================================
void HandleKeyConfigMenu(bool enterPressed, bool escPressed)
{
    if (rebindActionIdx >= 0)
    {
        // Waiting for keypress
        for (int k = (int)KeyboardKey.Space; k < 400; k++)
        {
            var key = (KeyboardKey)k;
            if (Raylib.IsKeyPressed(key))
            {
                SetBinding(rebindPlayer, rebindActionIdx, key);
                rebindActionIdx = -1;
                SaveBindings();
                return;
            }
        }
        return;
    }
    if (escPressed) { appState = AppState.StartMenu; return; }
    if (Raylib.IsKeyPressed(KeyboardKey.Up)) menuCursor = Math.Max(0, menuCursor - 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Down)) menuCursor = Math.Min(_actionNames.Length * 2 - 1, menuCursor + 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Tab)) { /* no-op for now */ }
    if (enterPressed)
    {
        rebindPlayer = menuCursor / _actionNames.Length;
        rebindActionIdx = menuCursor % _actionNames.Length;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.R))
    {
        // Reset to defaults
        bindings = new KeyBindingsConfig();
        players[0].Keys = bindings.P1;
        players[1].Keys = bindings.P2;
        SaveBindings();
    }
}

void SetBinding(int playerIdx, int actionIdx, KeyboardKey key)
{
    var kb = playerIdx == 0 ? bindings.P1 : bindings.P2;
    switch (actionIdx)
    {
        case 0: kb.Forward = key; break;
        case 1: kb.Back = key; break;
        case 2: kb.Left = key; break;
        case 3: kb.Right = key; break;
        case 4: kb.Sprint = key; break;
        case 5: kb.Reload = key; break;
        case 6: kb.CycleWeapon = key; break;
        case 7: kb.EnterTank = key; break;
        default:
            int wi = actionIdx - 8;
            if (wi >= 0 && wi < 6) kb.Weapons[wi] = key;
            break;
    }
    players[playerIdx].Keys = kb;
}

KeyboardKey GetBinding(int playerIdx, int actionIdx)
{
    var kb = playerIdx == 0 ? bindings.P1 : bindings.P2;
    return actionIdx switch
    {
        0 => kb.Forward,
        1 => kb.Back,
        2 => kb.Left,
        3 => kb.Right,
        4 => kb.Sprint,
        5 => kb.Reload,
        6 => kb.CycleWeapon,
        7 => kb.EnterTank,
        _ when actionIdx - 8 >= 0 && actionIdx - 8 < 6 => kb.Weapons[actionIdx - 8],
        _ => KeyboardKey.Null,
    };
}

void DrawKeyConfigMenu()
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color((byte)20, (byte)25, (byte)40, (byte)255));
    string title = "KEY BINDINGS";
    int tw = Raylib.MeasureText(title, 56);
    Raylib.DrawText(title, Width / 2 - tw / 2, 30, 56, Color.RayWhite);
    Raylib.DrawText("Player 1", Width / 4 - 60, 110, 36, Color.SkyBlue);
    Raylib.DrawText("Player 2", 3 * Width / 4 - 60, 110, 36, Color.Pink);

    int y0 = 170;
    int rowH = 32;
    for (int p = 0; p < 2; p++)
    {
        int colX = (p == 0 ? Width / 4 : 3 * Width / 4) - 240;
        for (int a = 0; a < _actionNames.Length; a++)
        {
            int idx = p * _actionNames.Length + a;
            bool sel = idx == menuCursor;
            Color c = sel ? Color.Yellow : Color.White;
            string line = $"{_actionNames[a],-15} {GetBinding(p, a)}";
            Raylib.DrawText(line, colX, y0 + a * rowH, 22, c);
        }
    }
    string hint = rebindActionIdx >= 0
        ? $"Press any key to bind  ({_actionNames[rebindActionIdx]} for P{rebindPlayer + 1})"
        : "[Up/Down] Navigate   [Enter] Rebind   [R] Reset defaults   [ESC] Back";
    int hw = Raylib.MeasureText(hint, 22);
    Raylib.DrawText(hint, Width / 2 - hw / 2, Height - 60, 22, rebindActionIdx >= 0 ? Color.Yellow : Color.Gray);
    Raylib.EndDrawing();
}

// =========================================================================
// State: IdentifyMice
// =========================================================================
void HandleIdentifyMice()
{
    // Look for any device that has clicked
    foreach (var kv in RawMouse.Devices)
    {
        if (!kv.Value.JustClicked) continue;
        if (identifyStage == 0)
        {
            players[0].MouseHandle = kv.Key;
            identifyStage = 1;
            kv.Value.JustClicked = false;
            return;
        }
        else if (identifyStage == 1 && kv.Key != players[0].MouseHandle)
        {
            players[1].MouseHandle = kv.Key;
            identifyStage = 2;
            ResetWorld();
            appState = AppState.Playing;
            return;
        }
    }
    // Reset transient flag for next frame
    foreach (var kv in RawMouse.Devices) kv.Value.JustClicked = false;

    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) appState = AppState.ModeMenu;
}

void DrawIdentifyMice()
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color((byte)20, (byte)25, (byte)40, (byte)255));
    string title = "MOUSE IDENTIFICATION";
    int tw = Raylib.MeasureText(title, 50);
    Raylib.DrawText(title, Width / 2 - tw / 2, 120, 50, Color.RayWhite);
    string prompt = identifyStage == 0
        ? "Player 1: click any button on your mouse"
        : "Player 2: click any button on your mouse  (different from P1's)";
    int pw = Raylib.MeasureText(prompt, 36);
    Raylib.DrawText(prompt, Width / 2 - pw / 2, Height / 2 - 18, 36, Color.Yellow);
    string sub = $"Detected mice so far: {RawMouse.Devices.Count}    Bound: {(identifyStage)}";
    int sw = Raylib.MeasureText(sub, 22);
    Raylib.DrawText(sub, Width / 2 - sw / 2, Height / 2 + 60, 22, Color.LightGray);
    string esc = "[ESC] Back";
    Raylib.DrawText(esc, Width / 2 - Raylib.MeasureText(esc, 22) / 2, Height - 60, 22, Color.Gray);
    Raylib.EndDrawing();
}

// =========================================================================
// State: Playing / Paused / GameOver
// =========================================================================
void HandlePlaying(float dt)
{
    bool pNow = Raylib.IsKeyDown(KeyboardKey.P) || Raylib.IsKeyDown(KeyboardKey.Escape);
    if (pNow && !prevP) { appState = AppState.Paused; prevP = pNow; return; }
    prevP = pNow;

    UpdateGame(dt);

    if (gameOver) { appState = AppState.GameOver; }
}

void HandlePaused()
{
    bool pNow = Raylib.IsKeyDown(KeyboardKey.P) || Raylib.IsKeyDown(KeyboardKey.Escape);
    if (pNow && !prevP) { appState = AppState.Playing; prevP = pNow; return; }
    prevP = pNow;
    if (Raylib.IsMouseButtonPressed(MouseButton.Left)) appState = AppState.Playing;
}

void HandleGameOver(bool enterPressed, bool escPressed)
{
    if (enterPressed || Raylib.IsMouseButtonPressed(MouseButton.Left))
    {
        appState = AppState.StartMenu;
    }
    if (escPressed) appState = AppState.StartMenu;
}

void DrawPauseOverlay()
{
    Raylib.BeginDrawing();
    Raylib.DrawRectangle(0, 0, Width, Height, new Color((byte)15, (byte)20, (byte)40, (byte)180));
    string t = "PAUSED";
    int tw = Raylib.MeasureText(t, 80);
    Raylib.DrawText(t, Width / 2 - tw / 2, Height / 2 - 80, 80, Color.White);
    string sub = "Click or press P / ESC to resume";
    int sw = Raylib.MeasureText(sub, 28);
    Raylib.DrawText(sub, Width / 2 - sw / 2, Height / 2 + 20, 28, Color.Yellow);
    Raylib.EndDrawing();
}

void DrawGameOverOverlay()
{
    Raylib.BeginDrawing();
    Raylib.DrawRectangle(0, 0, Width, Height, new Color((byte)0, (byte)0, (byte)0, (byte)150));
    string title;
    if (gameMode == GameMode.Versus)
    {
        int winner = players[0].Score >= VersusKillsToWin ? 0 : (players[1].Score >= VersusKillsToWin ? 1 : -1);
        if (winner == 0) title = "PLAYER 1 WINS!";
        else if (winner == 1) title = "PLAYER 2 WINS!";
        else if (players[0].Score > players[1].Score) title = "P1 WINS (time up)";
        else if (players[1].Score > players[0].Score) title = "P2 WINS (time up)";
        else title = "DRAW";
    }
    else
    {
        title = won ? "VICTORY!" : "DEFEAT";
    }
    Color col = title.Contains("VICTORY") || title.Contains("WINS") ? Color.Green : Color.Red;
    int tw = Raylib.MeasureText(title, 82);
    Raylib.DrawText(title, Width / 2 - tw / 2, Height / 2 - 120, 82, col);
    string sub = playerCount == 1
        ? $"Score {players[0].Score}     Enter / Click to continue"
        : $"P1 score {players[0].Score}     P2 score {players[1].Score}     Enter / Click to continue";
    int sw = Raylib.MeasureText(sub, 28);
    Raylib.DrawText(sub, Width / 2 - sw / 2, Height / 2 + 20, 28, Color.White);
    Raylib.EndDrawing();
}

// =========================================================================
// Reset / setup the world for a new game
// =========================================================================
void ResetWorld()
{
    enemies.Clear();
    enemyBullets.Clear();
    playerRockets.Clear();
    tracers.Clear();
    hitMarks.Clear();
    explosions.Clear();
    allies.Clear();
    healthPacks.Clear();
    weaponPickups.Clear();
    friendlyTanks.Clear();

    waveNum = 1;
    gameOver = false; won = false;
    waveDelay = 0;
    waveInProgress = false;
    bannerTimer = 0;
    versusTimer = 300f;

    // Player positions
    if (playerCount == 1)
    {
        players[0].Reset(new Vector3(0, 1.7f, 0));
    }
    else
    {
        players[0].Reset(new Vector3(-30, 1.7f, 0));
        players[1].Reset(new Vector3(30, 1.7f, 0));
    }
    for (int i = 0; i < playerCount; i++)
    {
        players[i].Score = 0;
        players[i].Keys = i == 0 ? bindings.P1 : bindings.P2;
        if (weaponPickupsMode && gameMode != GameMode.Versus)
        {
            // Pickups mode: start with pistol only
            for (int w = 0; w < weapons.Length; w++)
            {
                players[i].Owned[w] = (w == 0);
                players[i].Ammo[w] = (w == 0) ? weapons[w].MagSize : 0;
            }
            players[i].Weapon = 0;
        }
        else if (weaponPickupsMode && gameMode == GameMode.Versus)
        {
            for (int w = 0; w < weapons.Length; w++)
            {
                players[i].Owned[w] = (w == 0);
                players[i].Ammo[w] = (w == 0) ? weapons[w].MagSize : 0;
            }
            players[i].Weapon = 0;
        }
        else
        {
            for (int w = 0; w < weapons.Length; w++)
            {
                players[i].Owned[w] = true;
                players[i].Ammo[w] = weapons[w].MagSize;
            }
            players[i].Weapon = 0;
        }
    }

    // Tanks
    if (playerCount == 1)
        friendlyTanks.Add(new FriendlyTank { Pos = new Vector3(0, 0, 9), Yaw = 0 });
    else if (gameMode != GameMode.Versus)
    {
        friendlyTanks.Add(new FriendlyTank { Pos = new Vector3(-15, 0, -8), Yaw = 0 });
        friendlyTanks.Add(new FriendlyTank { Pos = new Vector3(15, 0, -8), Yaw = 0 });
    }

    // Health packs
    SpawnHealthPacks();

    // Enemies / waves (only in co-op)
    if (gameMode == GameMode.CoOp)
    {
        SpawnAllies();
        SpawnWave(waveNum);
    }

    // Weapon pickups
    if (weaponPickupsMode) SpawnWeaponPickups();
}

void SpawnHealthPacks()
{
    for (int i = 0; i < 12; i++)
    {
        var (x, z) = RandSpot(8, 0);
        healthPacks.Add(new HealthPack { Pos = new Vector3(x, 0, z), Active = true });
    }
}

void SpawnWeaponPickups()
{
    int count = 10;
    for (int i = 0; i < count; i++)
    {
        var (x, z) = RandSpot(12, 1.0f);
        int wi = 1 + rng.Next(5); // 1..5
        weaponPickups.Add(new WeaponPickup { Pos = new Vector3(x, 0, z), WeaponIdx = wi, Active = true });
    }
}

void SpawnAllies()
{
    if (playerCount == 1)
        allies.Add(new Ally { Pos = new Vector3(-3, 0, -3), Offset = new Vector3(-4, 0, -4), Hp = 170, MaxHp = 170, Alive = true });
    else
    {
        // One ally per player
        allies.Add(new Ally { Pos = new Vector3(-33, 0, -3), Offset = new Vector3(-4, 0, -4), Hp = 170, MaxHp = 170, Alive = true, FollowsPlayer = 0 });
        allies.Add(new Ally { Pos = new Vector3(33, 0, -3), Offset = new Vector3(4, 0, -4), Hp = 170, MaxHp = 170, Alive = true, FollowsPlayer = 1 });
    }
}

void SpawnWave(int wave)
{
    int shooters = 4 + wave * 2;
    int brawlers = 2 + wave;
    int tanks = 1 + wave / 2;
    if (wave == 1)
    {
        for (int i = 0; i < 2; i++)
        {
            float ang = (float)(rng.NextDouble() * MathF.PI * 2);
            float r = 14 + (float)rng.NextDouble() * 6;
            SpawnEnemyAt(EnemyType.Shooter, 70f + wave * 8, MathF.Cos(ang) * r, MathF.Sin(ang) * r);
        }
        shooters -= 2;
    }
    for (int i = 0; i < shooters; i++) SpawnEnemy(EnemyType.Shooter, 70f + wave * 8, 1.0f);
    for (int i = 0; i < brawlers; i++) SpawnEnemy(EnemyType.Brawler, 95f + wave * 10, 1.0f);
    for (int i = 0; i < tanks; i++) SpawnEnemy(EnemyType.Tank, 420f + wave * 55, 2.5f);
    ShowBanner($"WAVE {wave}", 2.0f);
    sfx.Play("waveStart");
    waveInProgress = true;
}

void SpawnEnemy(EnemyType type, float hp, float clearance)
{
    var (x, z) = RandSpot(24, clearance);
    SpawnEnemyAt(type, hp, x, z);
}

void SpawnEnemyAt(EnemyType type, float hp, float x, float z)
{
    float radius = type == EnemyType.Tank ? 1.8f : 0.55f;
    enemies.Add(new Enemy
    {
        Pos = new Vector3(x, 0, z), Hp = hp, MaxHp = hp, Type = type, Radius = radius,
        AttackCd = 0.8f + (float)rng.NextDouble(),
        StrafeTimer = (float)rng.NextDouble() * 2,
        StrafeDir = rng.Next(2) == 0, Alive = true,
    });
}

// =========================================================================
// World building
// =========================================================================
void BuildBattlefield()
{
    AddBox(new Vector3(0, 2, -ArenaSize), ArenaSize * 2, 4, 1, new Color((byte)90, (byte)80, (byte)65, (byte)255));
    AddBox(new Vector3(0, 2, ArenaSize), ArenaSize * 2, 4, 1, new Color((byte)90, (byte)80, (byte)65, (byte)255));
    AddBox(new Vector3(-ArenaSize, 2, 0), 1, 4, ArenaSize * 2, new Color((byte)90, (byte)80, (byte)65, (byte)255));
    AddBox(new Vector3(ArenaSize, 2, 0), 1, 4, ArenaSize * 2, new Color((byte)90, (byte)80, (byte)65, (byte)255));

    for (int i = 0; i < 46; i++)
    {
        var (x, z) = RandSpot(8, 0);
        float sx = 2f + (float)rng.NextDouble() * 5f;
        float sz = 2f + (float)rng.NextDouble() * 5f;
        float sy = 1.2f + (float)rng.NextDouble() * 4f;
        AddBox(new Vector3(x, sy / 2, z), sx, sy, sz, new Color((byte)115, (byte)95, (byte)80, (byte)255));
    }
    for (int i = 0; i < 22; i++)
    {
        var (x, z) = RandSpot(16, 0);
        bool longX = rng.NextDouble() < 0.5;
        float lng = 7f + (float)rng.NextDouble() * 9f;
        if (longX) AddBox(new Vector3(x, 0.55f, z), lng, 1.1f, 1.4f, new Color((byte)80, (byte)88, (byte)72, (byte)255));
        else AddBox(new Vector3(x, 0.55f, z), 1.4f, 1.1f, lng, new Color((byte)80, (byte)88, (byte)72, (byte)255));
    }
    for (int i = 0; i < 14; i++)
    {
        var (x, z) = RandSpot(18, 0);
        bool longX = rng.NextDouble() < 0.5;
        float lng = 5f + (float)rng.NextDouble() * 7f;
        if (longX) AddBox(new Vector3(x, 1.2f, z), lng, 2.4f, 1.0f, new Color((byte)115, (byte)95, (byte)80, (byte)255));
        else AddBox(new Vector3(x, 1.2f, z), 1.0f, 2.4f, lng, new Color((byte)115, (byte)95, (byte)80, (byte)255));
    }
    for (int i = 0; i < 6; i++)
    {
        var (cx, cz) = RandSpot(20, 0);
        float lng = 5f + (float)rng.NextDouble() * 3f;
        int dir = rng.Next(4);
        int dx = (dir == 0 || dir == 3) ? 1 : -1;
        int dz = (dir == 0 || dir == 1) ? 1 : -1;
        AddBox(new Vector3(cx + dx * lng / 2, 1.0f, cz), lng, 2.0f, 0.8f, new Color((byte)80, (byte)88, (byte)72, (byte)255));
        AddBox(new Vector3(cx, 1.0f, cz + dz * lng / 2), 0.8f, 2.0f, lng, new Color((byte)80, (byte)88, (byte)72, (byte)255));
    }

    platforms.Add(new TerrainBlock(new BoundingBox(new Vector3(-6, 0, -6), new Vector3(6, 0.1f, 6)), new Color((byte)80, (byte)95, (byte)80, (byte)255)));
    platforms.Add(new TerrainBlock(new BoundingBox(new Vector3(-52, 0, 30), new Vector3(-32, 0.2f, 46)), new Color((byte)45, (byte)60, (byte)85, (byte)255)));
    platforms.Add(new TerrainBlock(new BoundingBox(new Vector3(28, 0, -53), new Vector3(50, 0.2f, -37)), new Color((byte)45, (byte)60, (byte)85, (byte)255)));
}

void AddBox(Vector3 center, float sx, float sy, float sz, Color color)
{
    var box = new BoundingBox(center - new Vector3(sx / 2, sy / 2, sz / 2), center + new Vector3(sx / 2, sy / 2, sz / 2));
    platforms.Add(new TerrainBlock(box, color));
    obstacles.Add(box);
}

(float, float) RandSpot(float safe, float clearance)
{
    for (int i = 0; i < 80; i++)
    {
        float x = (float)(rng.NextDouble() * 2 - 1) * (ArenaSize - 5);
        float z = (float)(rng.NextDouble() * 2 - 1) * (ArenaSize - 5);
        if ((MathF.Abs(x) > safe || MathF.Abs(z) > safe) && !IsInsideAnyObstacle(x, z, clearance))
            return (x, z);
    }
    return (10f + (float)rng.NextDouble() * 20f, 10f + (float)rng.NextDouble() * 20f);
}

bool IsInsideAnyObstacle(float x, float z, float radius)
{
    foreach (var b in obstacles)
    {
        if (x > b.Min.X - radius && x < b.Max.X + radius
            && z > b.Min.Z - radius && z < b.Max.Z + radius) return true;
    }
    return false;
}

bool PointInObstacle(Vector3 p)
{
    foreach (var b in obstacles)
        if (p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z)
            return true;
    return false;
}

void ShowBanner(string text, float seconds)
{
    bannerText = text;
    bannerTimer = seconds;
}

// =========================================================================
// Game update loop
// =========================================================================
void UpdateGame(float dt)
{
    if (bannerTimer > 0) bannerTimer -= dt;

    if (gameMode == GameMode.Versus)
    {
        versusTimer -= dt;
        if (versusTimer <= 0) { gameOver = true; return; }
    }

    for (int i = 0; i < playerCount; i++) UpdatePlayer(players[i], dt);
    UpdateAllies(dt);
    UpdateEnemies(dt);
    UpdateProjectiles(dt);
    UpdateHealthPacks(dt);
    UpdateWeaponPickups(dt);
    CleanupEffects(dt);

    enemies.RemoveAll(e => !e.Alive);

    if (gameMode == GameMode.CoOp)
    {
        if (enemies.Count == 0 && waveInProgress)
        {
            waveInProgress = false;
            if (waveNum >= 8) { won = true; gameOver = true; sfx.Play("victory"); }
            else { waveDelay = 2.5f; ShowBanner($"WAVE {waveNum} CLEAR", 2.0f); sfx.Play("waveClear"); }
        }
        if (waveDelay > 0)
        {
            waveDelay -= dt;
            if (waveDelay <= 0) { waveNum++; SpawnWave(waveNum); }
        }
        // Co-op game over: all players dead at once (with no pending respawn)
        bool anyAlive = false;
        foreach (var p in players.Take(playerCount))
            if (p.Hp > 0 || p.RespawnTimer > 0) { anyAlive = true; break; }
        if (!anyAlive) { gameOver = true; sfx.Play("death"); }
    }
    else
    {
        // Versus: check score limit
        for (int i = 0; i < playerCount; i++)
            if (players[i].Score >= VersusKillsToWin) { gameOver = true; sfx.Play("victory"); break; }
    }
}

void UpdatePlayer(Player p, float dt)
{
    // Respawn handling
    if (p.Hp <= 0)
    {
        if (p.RespawnTimer > 0)
        {
            p.RespawnTimer -= dt;
            if (p.RespawnTimer <= 0) RespawnPlayer(p);
        }
        return;
    }

    // === Input deltas ===
    int mdx = 0, mdy = 0;
    bool lmbDown = false, lmbPressed = false, rmbDown = false;
    if (playerCount == 1)
    {
        // 1P: use Raylib mouse
        var md = Raylib.GetMouseDelta();
        mdx = (int)md.X; mdy = (int)md.Y;
        lmbDown = Raylib.IsMouseButtonDown(MouseButton.Left);
        lmbPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
        rmbDown = Raylib.IsMouseButtonDown(MouseButton.Right);
    }
    else
    {
        // 2P: use raw mouse
        if (RawMouse.Devices.TryGetValue(p.MouseHandle, out var dev))
        {
            (mdx, mdy) = dev.ConsumeDelta();
            lmbDown = dev.LeftDown;
            lmbPressed = dev.LeftPressedThisFrame;
            rmbDown = dev.RightDown;
            dev.LeftPressedThisFrame = false;
        }
    }

    float sensScale = p.CurrentFov / BaseFov;
    var tank = FindTankDriven(p.Id);
    if (p.InTank && tank != null && tank.Alive)
    {
        tank.Yaw -= mdx * 0.0022f * sensScale;
        p.Pitch -= mdy * 0.0022f * sensScale;
        p.Pitch = Math.Clamp(p.Pitch, -1.0f, 1.0f);
        p.Yaw = tank.Yaw;
    }
    else
    {
        p.Yaw -= mdx * 0.0028f * sensScale;
        p.Pitch -= mdy * 0.0028f * sensScale;
        p.Pitch = Math.Clamp(p.Pitch, -1.4f, 1.4f);
    }
    p.RmbDown = rmbDown;

    // FOV ADS
    float fovTarget = BaseFov;
    var w = weapons[p.Weapon];
    if (rmbDown && !p.InTank) fovTarget = w.Sniper ? 18f : 50f;
    p.CurrentFov += (fovTarget - p.CurrentFov) * 0.25f;
    if (MathF.Abs(p.CurrentFov - fovTarget) < 0.1f) p.CurrentFov = fovTarget;

    Vector3 forward = new(MathF.Cos(p.Pitch) * MathF.Sin(p.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(p.Yaw));
    Vector3 flatFwd = Vector3.Normalize(new Vector3(MathF.Sin(p.Yaw), 0, MathF.Cos(p.Yaw)));
    Vector3 right = Vector3.Normalize(Vector3.Cross(flatFwd, Vector3.UnitY));

    // Tank enter/exit
    bool fNow = Raylib.IsKeyDown(p.Keys.EnterTank);
    if (fNow && !p.PrevF)
    {
        if (!p.InTank)
        {
            // Find nearest unmanned tank within 5m
            FriendlyTank? near = null;
            float bd = 5f;
            foreach (var t in friendlyTanks)
            {
                if (!t.Alive || t.Driver != null) continue;
                float d = Vector3.Distance(p.Pos, t.Pos);
                if (d < bd) { bd = d; near = t; }
            }
            if (near != null) { near.Driver = p.Id; p.InTank = true; p.Pos = near.Pos; p.Yaw = near.Yaw; }
        }
        else
        {
            if (tank != null)
            {
                tank.Driver = null;
                var side = new Vector3(MathF.Sin(tank.Yaw + MathF.PI / 2), 0, MathF.Cos(tank.Yaw + MathF.PI / 2));
                p.Pos = tank.Pos + side * 4f + new Vector3(0, 1.7f, 0);
            }
            p.InTank = false;
        }
    }
    p.PrevF = fNow;

    if (p.InTank && tank != null && tank.Alive)
    {
        float turn = 0;
        if (Raylib.IsKeyDown(p.Keys.Left)) turn += 1;
        if (Raylib.IsKeyDown(p.Keys.Right)) turn -= 1;
        tank.Yaw += turn * 1.2f * dt;
        var tFwd = new Vector3(MathF.Sin(tank.Yaw), 0, MathF.Cos(tank.Yaw));
        float drive = 0;
        if (Raylib.IsKeyDown(p.Keys.Forward)) drive += 1;
        if (Raylib.IsKeyDown(p.Keys.Back)) drive -= 0.55f;
        var next = tank.Pos + tFwd * drive * 7.5f * dt;
        next.X = Math.Clamp(next.X, -ArenaSize + 3, ArenaSize - 3);
        next.Z = Math.Clamp(next.Z, -ArenaSize + 3, ArenaSize - 3);
        tank.Pos = next;
        p.Pos = tank.Pos; p.Yaw = tank.Yaw;

        if (tank.FireCd > 0) tank.FireCd -= dt;
        if (lmbPressed && tank.FireCd <= 0)
        {
            tank.FireCd = 1.1f;
            sfx.Play("tankShell");
            var dir = Vector3.Normalize(new Vector3(MathF.Cos(p.Pitch) * MathF.Sin(tank.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(tank.Yaw)));
            var muzzle = tank.Pos + new Vector3(0, 2.0f, 0) + dir * 3.2f;
            playerRockets.Add(new Rocket { Pos = muzzle, PrevPos = muzzle, Dir = dir, Life = 5f, Damage = 220, Radius = 10f, Big = true, OwnerPlayer = p.Id });
        }
    }
    else
    {
        // On-foot move
        Vector3 move = Vector3.Zero;
        if (Raylib.IsKeyDown(p.Keys.Forward)) move += flatFwd;
        if (Raylib.IsKeyDown(p.Keys.Back)) move -= flatFwd;
        if (Raylib.IsKeyDown(p.Keys.Right)) move += right;
        if (Raylib.IsKeyDown(p.Keys.Left)) move -= right;
        if (move.LengthSquared() > 0.0001f) move = Vector3.Normalize(move);
        float speed = Raylib.IsKeyDown(p.Keys.Sprint) ? 9.5f : 6.2f;
        MovePlayer(p, move * speed * dt);
        foreach (var ft in friendlyTanks) PushOutOfTank(ref p.Pos, PlayerRadius, ft);

        // Weapon switching
        for (int i = 0; i < weapons.Length; i++)
            if (Raylib.IsKeyPressed(p.Keys.Weapons[i]) && p.Owned[i]) p.Weapon = i;
        bool qNow = Raylib.IsKeyDown(p.Keys.CycleWeapon);
        if (qNow && !p.PrevQ)
        {
            for (int step = 1; step <= weapons.Length; step++)
            {
                int nw = (p.Weapon + step) % weapons.Length;
                if (p.Owned[nw]) { p.Weapon = nw; break; }
            }
        }
        p.PrevQ = qNow;

        var weapon = weapons[p.Weapon];
        if (p.Reload > 0)
        {
            p.Reload -= dt;
            if (p.Reload <= 0 && weapon.MagSize > 0) p.Ammo[p.Weapon] = weapon.MagSize;
        }
        if (weapon.MagSize > 0 && Raylib.IsKeyPressed(p.Keys.Reload)
            && p.Ammo[p.Weapon] < weapon.MagSize && p.Reload <= 0)
        {
            p.Reload = weapon.ReloadTime;
            sfx.Play("reload");
        }
        if (p.Cooldown > 0) p.Cooldown -= dt;
        if (p.SwordAnim > 0) p.SwordAnim -= dt;

        Vector3 eye = p.Pos + new Vector3(0, 0.2f, 0);
        bool attacking = weapon.Full ? lmbDown : lmbPressed;
        if (attacking && p.Cooldown <= 0 && p.Reload <= 0)
            FirePlayerWeapon(p, weapon, eye, forward, right);
    }

    // Damage indicator fade
    for (int i = p.DmgInds.Count - 1; i >= 0; i--)
    {
        p.DmgInds[i].Life -= dt;
        if (p.DmgInds[i].Life <= 0) p.DmgInds.RemoveAt(i);
    }
}

void RespawnPlayer(Player p)
{
    p.Hp = PlayerMaxHp;
    p.Cooldown = 0; p.Reload = 0; p.SwordAnim = 0;
    p.InTank = false;
    if (gameMode == GameMode.Versus)
    {
        var (x, z) = RandSpot(20, 0.5f);
        p.Pos = new Vector3(x, 1.7f, z);
    }
    else
    {
        // Co-op: respawn near surviving player or initial spawn
        Player? other = null;
        for (int i = 0; i < playerCount; i++) if (players[i] != p && players[i].Hp > 0) { other = players[i]; break; }
        if (other != null) p.Pos = other.Pos + new Vector3(2, 0, 0);
        else p.Pos = new Vector3(p.Id == 0 ? -30 : 30, 1.7f, 0);
    }
}

FriendlyTank? FindTankDriven(int playerId)
{
    foreach (var t in friendlyTanks) if (t.Alive && t.Driver == playerId) return t;
    return null;
}

// =========================================================================
// Combat
// =========================================================================
void FirePlayerWeapon(Player p, Weapon weapon, Vector3 eye, Vector3 forward, Vector3 right)
{
    p.Cooldown = weapon.Cooldown;
    if (weapon.Melee)
    {
        p.SwordAnim = 0.3f;
        sfx.Play("sword");
        foreach (var e in enemies)
        {
            if (!e.Alive) continue;
            var to = e.Pos + new Vector3(0, 1, 0) - eye;
            float d = to.Length();
            if (d < weapon.Range && Vector3.Dot(to / d, forward) > 0.52f)
                DamageEnemy(e, weapon.Damage, e.Pos + new Vector3(0, 1, 0), p.Id);
        }
        // PvP sword hit
        if (gameMode == GameMode.Versus)
        {
            for (int i = 0; i < playerCount; i++)
            {
                if (i == p.Id) continue;
                var other = players[i];
                if (other.Hp <= 0) continue;
                var to = other.Pos + new Vector3(0, 0.9f, 0) - eye;
                float d = to.Length();
                if (d < weapon.Range && Vector3.Dot(to / d, forward) > 0.52f)
                    DamagePlayer(other, weapon.Damage, eye, p.Id);
            }
        }
        return;
    }
    if (p.Ammo[p.Weapon] <= 0)
    {
        sfx.Play("empty");
        p.Reload = weapon.ReloadTime;
        return;
    }
    p.Ammo[p.Weapon]--;
    sfx.Play(weapon.SfxKey);
    Vector3 dir = ApplySpread(forward, weapon.Spread);
    Vector3 muzzle = eye + right * 0.28f - Vector3.UnitY * 0.16f + dir * 0.5f;
    if (weapon.Explosive)
    {
        playerRockets.Add(new Rocket { Pos = muzzle, PrevPos = muzzle, Dir = dir, Life = 5f, Damage = weapon.Damage, Radius = 7.5f, Big = false, OwnerPlayer = p.Id });
        return;
    }
    Vector3 hitPoint = eye + dir * weapon.Range;
    Enemy? hitE = null;
    Player? hitP = null;
    float bestT = weapon.Range;
    foreach (var e in enemies)
    {
        if (!e.Alive) continue;
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, dir), e.Bounds);
        if (rc.Hit && rc.Distance < bestT) { bestT = rc.Distance; hitE = e; hitP = null; hitPoint = rc.Point; }
    }
    if (gameMode == GameMode.Versus)
    {
        for (int i = 0; i < playerCount; i++)
        {
            if (i == p.Id) continue;
            var other = players[i];
            if (other.Hp <= 0) continue;
            var bb = new BoundingBox(other.Pos + new Vector3(-0.4f, -1.7f, -0.4f), other.Pos + new Vector3(0.4f, 0.3f, 0.4f));
            var rc = Raylib.GetRayCollisionBox(new Ray(eye, dir), bb);
            if (rc.Hit && rc.Distance < bestT) { bestT = rc.Distance; hitE = null; hitP = other; hitPoint = rc.Point; }
        }
    }
    foreach (var b in obstacles)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, dir), b);
        if (rc.Hit && rc.Distance < bestT) { bestT = rc.Distance; hitE = null; hitP = null; hitPoint = rc.Point; }
    }
    if (hitE != null) DamageEnemy(hitE, weapon.Damage, hitPoint, p.Id);
    if (hitP != null) DamagePlayer(hitP, weapon.Damage, hitPoint, p.Id);
    Color trColor = weapon.Sniper ? Color.White : weapon.Name == "Machine Gun" ? Color.Orange : Color.Yellow;
    tracers.Add(new Tracer { From = muzzle, To = hitPoint, Life = 0.06f, Color = trColor });
}

void DamageEnemy(Enemy e, float dmg, Vector3 hitPoint, int attackerPlayerId)
{
    if (!e.Alive) return;
    e.Hp -= dmg;
    e.HitFlash = 0.13f;
    sfx.Play("hit");
    hitMarks.Add(new HitMark { Pos = hitPoint, Life = 0.25f });
    if (e.Hp <= 0)
    {
        e.Alive = false;
        sfx.Play("enemyHurt");
        if (attackerPlayerId >= 0 && attackerPlayerId < playerCount)
        {
            int kill = e.Type == EnemyType.Tank ? 550 : e.Type == EnemyType.Brawler ? 150 : 100;
            players[attackerPlayerId].Score += kill;
        }
        if (e.Type == EnemyType.Tank) SpawnExplosion(e.Center, 8f, 0f, -1);
    }
}

void DamagePlayer(Player target, float dmg, Vector3 source, int attackerPlayerId)
{
    target.Hp -= dmg;
    target.AddDamageIndicator(source);
    sfx.Play("playerHurt");
    if (target.Hp <= 0)
    {
        target.Hp = 0;
        target.RespawnTimer = gameMode == GameMode.Versus ? VersusRespawnDelay : CoopReviveDelay;
        if (gameMode == GameMode.Versus && attackerPlayerId >= 0 && attackerPlayerId < playerCount && attackerPlayerId != target.Id)
            players[attackerPlayerId].Score += 1;
        if (target.InTank)
        {
            var tank = FindTankDriven(target.Id);
            if (tank != null) tank.Driver = null;
            target.InTank = false;
        }
    }
}

void DamageFriendlyAt(Vector3 target, float dmg, Vector3 source)
{
    foreach (var ft in friendlyTanks)
    {
        if (ft.Alive && Vector3.DistanceSquared(target, ft.Pos) < 3.0f * 3.0f)
        {
            ft.Hp -= dmg;
            if (ft.Driver != null && ft.Driver.Value < playerCount)
            {
                var driver = players[ft.Driver.Value];
                driver.AddDamageIndicator(source);
                sfx.Play("playerHurt");
            }
            if (ft.Hp <= 0) HandleTankDestroyed(ft);
            return;
        }
    }
    for (int i = 0; i < playerCount; i++)
    {
        var p = players[i];
        if (p.Hp <= 0) continue;
        if (Vector3.DistanceSquared(target, p.Pos) < 1.5f * 1.5f)
        {
            DamagePlayer(p, dmg, source, -1);
            return;
        }
    }
    foreach (var a in allies)
    {
        if (a.Alive && Vector3.DistanceSquared(target, a.Pos) < 1.7f * 1.7f)
        {
            a.Hp -= dmg;
            if (a.Hp <= 0) { a.Alive = false; a.Respawn = 8f; }
            return;
        }
    }
}

void HandleTankDestroyed(FriendlyTank ft)
{
    ft.Alive = false;
    SpawnExplosion(ft.Pos + new Vector3(0, 1.5f, 0), 9f, 0f, -1);
    if (ft.Driver != null && ft.Driver.Value < playerCount)
    {
        var p = players[ft.Driver.Value];
        p.InTank = false;
        p.Hp = MathF.Max(1, p.Hp - 25);
        p.Pos = ft.Pos + new Vector3(3, 1.7f, 0);
        ft.Driver = null;
    }
}

void SpawnExplosion(Vector3 pos, float radius, float damage, int attackerPlayerId)
{
    sfx.Play("explode");
    explosions.Add(new Explosion { Pos = pos, MaxRadius = radius, Life = 0.5f, MaxLife = 0.5f });
    if (damage > 0)
    {
        foreach (var e in enemies)
        {
            if (!e.Alive) continue;
            float d = Vector3.Distance(e.Center, pos);
            if (d <= radius) DamageEnemy(e, damage * (1f - d / radius * 0.55f), e.Pos, attackerPlayerId);
        }
        if (gameMode == GameMode.Versus)
        {
            for (int i = 0; i < playerCount; i++)
            {
                if (i == attackerPlayerId) continue;
                var p = players[i];
                if (p.Hp <= 0) continue;
                float d = Vector3.Distance(p.Pos, pos);
                if (d <= radius)
                {
                    float dmg = damage * 0.7f * (1f - d / radius);
                    DamagePlayer(p, dmg, pos, attackerPlayerId);
                }
            }
        }
        // Self-damage (no friendly fire to attacker in co-op, but explosion still hurts at close range)
        if (attackerPlayerId >= 0 && attackerPlayerId < playerCount)
        {
            var self = players[attackerPlayerId];
            float d = Vector3.Distance(self.Pos, pos);
            if (d < radius * 0.5f)
            {
                float dmg = damage * 0.5f * (1f - d / radius);
                DamagePlayer(self, dmg, pos, -1);
            }
        }
    }
}

void FireEnemyBullet(Vector3 from, Vector3 target, float damage, float speed, float radius)
{
    var dir = Vector3.Normalize(target - from);
    dir += new Vector3((float)(rng.NextDouble() - 0.5) * 0.05f, (float)(rng.NextDouble() - 0.5) * 0.04f, (float)(rng.NextDouble() - 0.5) * 0.05f);
    dir = Vector3.Normalize(dir);
    enemyBullets.Add(new Bullet { Pos = from + dir * 0.7f, Dir = dir, Speed = speed, Damage = damage, Radius = radius, Life = 5f });
}

// =========================================================================
// Enemy & Ally update
// =========================================================================
void UpdateEnemies(float dt)
{
    foreach (var e in enemies)
    {
        if (!e.Alive) continue;
        if (e.HitFlash > 0) e.HitFlash -= dt;
        e.AttackCd -= dt;
        e.StrafeTimer -= dt;
        e.Vel *= MathF.Pow(0.04f, dt);
        Vector3 target = ChooseEnemyTarget(e.Pos);
        Vector3 to = target - e.Pos; to.Y = 0;
        float d = to.Length();
        Vector3 dn = d > 0.01f ? to / d : Vector3.Zero;
        if (e.Type == EnemyType.Shooter)
        {
            if (d > 18) MoveEnemy(e, dn * 2.7f * dt);
            else if (d < 11) MoveEnemy(e, -dn * 2.2f * dt);
            else
            {
                if (e.StrafeTimer <= 0) { e.StrafeTimer = 1.3f + (float)rng.NextDouble(); e.StrafeDir = !e.StrafeDir; }
                var s = new Vector3(-dn.Z, 0, dn.X);
                MoveEnemy(e, s * (e.StrafeDir ? 1.9f : -1.9f) * dt);
            }
            if (e.AttackCd <= 0 && d < 45f)
            {
                e.AttackCd = 1.1f;
                FireEnemyBullet(e.Pos + new Vector3(0, 1.1f, 0), target + new Vector3(0, 0.5f, 0), 12, 26f, 0.1f);
            }
        }
        else if (e.Type == EnemyType.Brawler)
        {
            if (d > 1.9f) MoveEnemy(e, dn * 5.2f * dt);
            if (d < 2.3f && e.AttackCd <= 0)
            {
                e.AttackCd = 0.85f;
                DamageFriendlyAt(target, 18, e.Pos);
            }
        }
        else
        {
            if (d > 28) MoveEnemy(e, dn * 1.4f * dt);
            else if (d < 16) MoveEnemy(e, -dn * 0.8f * dt);
            if (e.AttackCd <= 0 && d < 65f)
            {
                e.AttackCd = 2.4f;
                FireEnemyBullet(e.Pos + new Vector3(0, 2.0f, 0), target + new Vector3(0, 0.4f, 0), 38, 18f, 0.22f);
            }
        }
        MoveEnemy(e, e.Vel * dt);
        e.Pos.X = Math.Clamp(e.Pos.X, -ArenaSize + 2, ArenaSize - 2);
        e.Pos.Z = Math.Clamp(e.Pos.Z, -ArenaSize + 2, ArenaSize - 2);

        // Push out of every friendly tank + ramming damage
        foreach (var ft in friendlyTanks)
        {
            if (!ft.Alive) continue;
            bool pushed = TryPushOutOfTank(e, ft);
            if (pushed && ft.Driver != null) DamageEnemy(e, 110f * dt, e.Pos + new Vector3(0, 1, 0), ft.Driver.Value);
        }

        // Stuck rescue
        if (IsInsideAnyObstacle(e.Pos.X, e.Pos.Z, e.Radius * 0.4f))
        {
            bool freed = false;
            for (float ring = 1.5f; ring <= 12f; ring += 1f)
            {
                for (int a = 0; a < 12; a++)
                {
                    float ang = a * MathF.PI / 6f + (float)rng.NextDouble() * 0.3f;
                    float tx = e.Pos.X + MathF.Cos(ang) * ring;
                    float tz = e.Pos.Z + MathF.Sin(ang) * ring;
                    if (!IsInsideAnyObstacle(tx, tz, e.Radius)
                        && MathF.Abs(tx) < ArenaSize - 2 && MathF.Abs(tz) < ArenaSize - 2)
                    {
                        e.Pos.X = tx; e.Pos.Z = tz; freed = true; break;
                    }
                }
                if (freed) break;
            }
            if (!freed) { var (rx, rz) = RandSpot(24, e.Radius); e.Pos.X = rx; e.Pos.Z = rz; }
        }
    }
}

Vector3 ChooseEnemyTarget(Vector3 from)
{
    Vector3 best = Vector3.Zero;
    float bd = float.MaxValue;
    for (int i = 0; i < playerCount; i++)
    {
        if (players[i].Hp <= 0) continue;
        float d = Vector3.DistanceSquared(from, players[i].Pos);
        if (d < bd) { bd = d; best = players[i].Pos; }
    }
    if (bd == float.MaxValue) best = Vector3.Zero;
    foreach (var a in allies)
    {
        if (!a.Alive) continue;
        float d = Vector3.DistanceSquared(from, a.Pos);
        if (d < bd && rng.NextDouble() < 0.7) { bd = d; best = a.Pos; }
    }
    foreach (var ft in friendlyTanks)
    {
        if (!ft.Alive) continue;
        float d = Vector3.DistanceSquared(from, ft.Pos);
        if (d < bd && rng.NextDouble() < 0.4) { bd = d; best = ft.Pos; }
    }
    return best;
}

void UpdateAllies(float dt)
{
    foreach (var a in allies)
    {
        if (!a.Alive)
        {
            a.Respawn -= dt;
            if (a.Respawn <= 0)
            {
                a.Hp = a.MaxHp;
                var leader = (a.FollowsPlayer >= 0 && a.FollowsPlayer < playerCount) ? players[a.FollowsPlayer] : players[0];
                float ang = (float)(rng.NextDouble() * MathF.PI * 2);
                float r = 4 + (float)rng.NextDouble() * 3;
                a.Pos = leader.Pos + new Vector3(MathF.Cos(ang) * r, 0, MathF.Sin(ang) * r);
                a.Alive = true;
            }
            continue;
        }
        Enemy? target = enemies.Where(e => e.Alive).OrderBy(e => Vector3.DistanceSquared(e.Pos, a.Pos)).FirstOrDefault();
        var leader2 = (a.FollowsPlayer >= 0 && a.FollowsPlayer < playerCount) ? players[a.FollowsPlayer] : players[0];
        Vector3 desired = leader2.Pos + a.Offset;
        if (target != null && Vector3.Distance(a.Pos, target.Pos) < 30f) desired = a.Pos;
        Vector3 toDesired = desired - a.Pos; toDesired.Y = 0;
        if (toDesired.Length() > 3f)
        {
            var m = Vector3.Normalize(toDesired) * 3.8f * dt;
            MoveCircle(ref a.Pos, new Vector3(m.X, 0, 0), 0.45f);
            MoveCircle(ref a.Pos, new Vector3(0, 0, m.Z), 0.45f);
        }
        a.FireCd -= dt;
        if (target != null && a.FireCd <= 0 && Vector3.Distance(a.Pos, target.Pos) < 45f)
        {
            a.FireCd = 0.28f;
            var from = a.Pos + new Vector3(0, 1.25f, 0);
            DamageEnemy(target, 8, target.Pos, a.FollowsPlayer >= 0 ? a.FollowsPlayer : 0);
            tracers.Add(new Tracer { From = from, To = target.Pos + new Vector3(0, 1, 0), Life = 0.06f, Color = Color.SkyBlue });
        }
    }
}

void UpdateProjectiles(float dt)
{
    for (int i = enemyBullets.Count - 1; i >= 0; i--)
    {
        var b = enemyBullets[i];
        b.Pos += b.Dir * b.Speed * dt;
        b.Life -= dt;
        bool hit = false;
        // Check player hit
        for (int j = 0; j < playerCount && !hit; j++)
        {
            var p = players[j];
            if (p.Hp <= 0) continue;
            // If in tank, check tank box; else player sphere
            if (p.InTank)
            {
                var ft = FindTankDriven(p.Id);
                if (ft != null && Vector3.Distance(b.Pos, ft.Pos + new Vector3(0, 1.3f, 0)) < 2.2f + b.Radius)
                {
                    ft.Hp -= b.Damage;
                    p.AddDamageIndicator(b.Pos);
                    sfx.Play("playerHurt");
                    if (ft.Hp <= 0) HandleTankDestroyed(ft);
                    hit = true;
                }
            }
            else if (Vector3.Distance(b.Pos, p.Pos + new Vector3(0, 0.7f, 0)) < 0.65f + b.Radius)
            {
                DamagePlayer(p, b.Damage, b.Pos, -1);
                hit = true;
            }
        }
        if (!hit)
        {
            foreach (var a in allies)
            {
                if (!a.Alive) continue;
                if (Vector3.Distance(b.Pos, a.Pos + new Vector3(0, 1, 0)) < 0.65f + b.Radius)
                {
                    a.Hp -= b.Damage;
                    if (a.Hp <= 0) { a.Alive = false; a.Respawn = 8; }
                    hit = true; break;
                }
            }
        }
        if (hit || b.Life <= 0 || PointInObstacle(b.Pos) || MathF.Abs(b.Pos.X) > ArenaSize || MathF.Abs(b.Pos.Z) > ArenaSize)
            enemyBullets.RemoveAt(i);
    }
    for (int i = playerRockets.Count - 1; i >= 0; i--)
    {
        var r = playerRockets[i];
        r.PrevPos = r.Pos;
        r.Pos += r.Dir * 31f * dt;
        r.Life -= dt;
        bool explode = r.Life <= 0 || PointInObstacle(r.Pos)
            || MathF.Abs(r.Pos.X) > ArenaSize || MathF.Abs(r.Pos.Z) > ArenaSize;
        foreach (var e in enemies)
        {
            if (e.Alive && Vector3.Distance(e.Pos, r.Pos) < (e.Type == EnemyType.Tank ? 2.4f : 1.2f))
            { explode = true; break; }
        }
        if (gameMode == GameMode.Versus)
        {
            for (int j = 0; j < playerCount; j++)
            {
                if (j == r.OwnerPlayer) continue;
                var p = players[j];
                if (p.Hp <= 0) continue;
                if (Vector3.Distance(p.Pos, r.Pos) < 1.5f) { explode = true; break; }
            }
        }
        if (explode) { SpawnExplosion(r.Pos, r.Radius, r.Damage, r.OwnerPlayer); playerRockets.RemoveAt(i); }
    }
}

void UpdateHealthPacks(float dt)
{
    foreach (var pk in healthPacks)
    {
        if (!pk.Active)
        {
            pk.Respawn -= dt;
            if (pk.Respawn <= 0) pk.Active = true;
            continue;
        }
        for (int i = 0; i < playerCount; i++)
        {
            var p = players[i];
            if (p.Hp <= 0 || p.Hp >= PlayerMaxHp) continue;
            if (Vector3.Distance(p.Pos, pk.Pos) < 2.0f)
            {
                p.Hp = MathF.Min(PlayerMaxHp, p.Hp + 55);
                pk.Active = false; pk.Respawn = 18f;
                sfx.Play("pickup");
                break;
            }
        }
    }
}

void UpdateWeaponPickups(float dt)
{
    foreach (var wp in weaponPickups)
    {
        if (!wp.Active)
        {
            wp.Respawn -= dt;
            if (wp.Respawn <= 0)
            {
                wp.Active = true;
                wp.WeaponIdx = 1 + rng.Next(5);
            }
            continue;
        }
        for (int i = 0; i < playerCount; i++)
        {
            var p = players[i];
            if (p.Hp <= 0) continue;
            if (Vector3.Distance(p.Pos, wp.Pos) < 2.0f)
            {
                p.Owned[wp.WeaponIdx] = true;
                p.Ammo[wp.WeaponIdx] = weapons[wp.WeaponIdx].MagSize;
                wp.Active = false; wp.Respawn = 22f;
                sfx.Play("pickup");
                break;
            }
        }
    }
}

void CleanupEffects(float dt)
{
    for (int i = tracers.Count - 1; i >= 0; i--) { tracers[i].Life -= dt; if (tracers[i].Life <= 0) tracers.RemoveAt(i); }
    for (int i = hitMarks.Count - 1; i >= 0; i--) { hitMarks[i].Life -= dt; if (hitMarks[i].Life <= 0) hitMarks.RemoveAt(i); }
    for (int i = explosions.Count - 1; i >= 0; i--) { explosions[i].Life -= dt; if (explosions[i].Life <= 0) explosions.RemoveAt(i); }
}

// =========================================================================
// Movement helpers
// =========================================================================
void MovePlayer(Player p, Vector3 delta)
{
    MoveCircle(ref p.Pos, new Vector3(delta.X, 0, 0), PlayerRadius);
    MoveCircle(ref p.Pos, new Vector3(0, 0, delta.Z), PlayerRadius);
    p.Pos.X = Math.Clamp(p.Pos.X, -ArenaSize + 1, ArenaSize - 1);
    p.Pos.Z = Math.Clamp(p.Pos.Z, -ArenaSize + 1, ArenaSize - 1);
}

void MoveEnemy(Enemy e, Vector3 delta)
{
    Vector3 v = e.Pos;
    MoveCircle(ref v, new Vector3(delta.X, 0, 0), e.Radius);
    MoveCircle(ref v, new Vector3(0, 0, delta.Z), e.Radius);
    e.Pos = v;
}

void MoveCircle(ref Vector3 pos, Vector3 delta, float radius)
{
    Vector3 np = pos + delta;
    foreach (var b in obstacles)
    {
        bool xIn = np.X > b.Min.X - radius && np.X < b.Max.X + radius;
        bool zIn = np.Z > b.Min.Z - radius && np.Z < b.Max.Z + radius;
        if (xIn && zIn) return;
    }
    pos = np;
}

void PushOutOfTank(ref Vector3 pos, float radius, FriendlyTank ft)
{
    if (!ft.Alive) return;
    float dx = pos.X - ft.Pos.X;
    float dz = pos.Z - ft.Pos.Z;
    float d = MathF.Sqrt(dx * dx + dz * dz);
    float minD = TankCollideR + radius;
    if (d >= minD) return;
    if (d < 0.0001f) { pos.X += minD; return; }
    pos.X = ft.Pos.X + dx / d * minD;
    pos.Z = ft.Pos.Z + dz / d * minD;
}

bool TryPushOutOfTank(Enemy e, FriendlyTank ft)
{
    if (!ft.Alive) return false;
    float dx = e.Pos.X - ft.Pos.X;
    float dz = e.Pos.Z - ft.Pos.Z;
    float d = MathF.Sqrt(dx * dx + dz * dz);
    float minD = TankCollideR + e.Radius;
    if (d >= minD) return false;
    if (d < 0.0001f) { e.Pos.X += minD; return true; }
    e.Pos.X = ft.Pos.X + dx / d * minD;
    e.Pos.Z = ft.Pos.Z + dz / d * minD;
    return true;
}

Vector3 ApplySpread(Vector3 dir, float spread)
{
    if (spread <= 0) return Vector3.Normalize(dir);
    var d = dir + new Vector3(
        ((float)rng.NextDouble() - 0.5f) * spread,
        ((float)rng.NextDouble() - 0.5f) * spread,
        ((float)rng.NextDouble() - 0.5f) * spread);
    return Vector3.Normalize(d);
}

// =========================================================================
// Rendering
// =========================================================================
void RenderSplit()
{
    if (playerCount == 1)
    {
        // Render directly to screen
        var p = players[0];
        Vector3 forward = new(MathF.Cos(p.Pitch) * MathF.Sin(p.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(p.Yaw));
        Vector3 eye = p.InTank && FindTankDriven(p.Id) is FriendlyTank ft ? ft.Pos + new Vector3(0, 2.7f, 0) : p.Pos + new Vector3(0, 0.2f, 0);
        var cam = new Camera3D(eye, eye + forward, Vector3.UnitY, p.CurrentFov, CameraProjection.Perspective);
        Raylib.BeginDrawing();
        Raylib.ClearBackground(new Color((byte)35, (byte)45, (byte)65, (byte)255));
        Raylib.BeginMode3D(cam);
        DrawWorld();
        Raylib.EndMode3D();
        DrawHudFor(p, 0, 0, Width, Height);
        Raylib.EndDrawing();
    }
    else
    {
        // Render each player to their RT, then composite
        RenderPlayerTo(rt0, players[0], rtW0, rtH0);
        RenderPlayerTo(rt1, players[1], rtW1, rtH1);
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        if (splitMode == SplitMode.Horizontal)
        {
            var src0 = new Rectangle(0, 0, rtW0, -rtH0);
            var src1 = new Rectangle(0, 0, rtW1, -rtH1);
            Raylib.DrawTextureRec(rt0.Texture, src0, new Vector2(0, 0), Color.White);
            Raylib.DrawTextureRec(rt1.Texture, src1, new Vector2(rtW0, 0), Color.White);
            Raylib.DrawRectangle(rtW0 - 1, 0, 2, Height, Color.Black);
        }
        else
        {
            var src0 = new Rectangle(0, 0, rtW0, -rtH0);
            var src1 = new Rectangle(0, 0, rtW1, -rtH1);
            Raylib.DrawTextureRec(rt0.Texture, src0, new Vector2(0, 0), Color.White);
            Raylib.DrawTextureRec(rt1.Texture, src1, new Vector2(0, rtH0), Color.White);
            Raylib.DrawRectangle(0, rtH0 - 1, Width, 2, Color.Black);
        }
        // Banner across full screen
        if (bannerTimer > 0 && !string.IsNullOrEmpty(bannerText))
        {
            int fs = 64;
            int tw = Raylib.MeasureText(bannerText, fs);
            byte alpha = (byte)(MathF.Min(1f, bannerTimer / 0.5f) * 255);
            Raylib.DrawText(bannerText, Width / 2 - tw / 2, (int)(Height * 0.18f), fs, new Color((byte)255, (byte)240, (byte)100, alpha));
        }
        Raylib.EndDrawing();
    }
}

void RenderPlayerTo(RenderTexture2D rt, Player p, int rtW, int rtH)
{
    Vector3 forward = new(MathF.Cos(p.Pitch) * MathF.Sin(p.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(p.Yaw));
    Vector3 eye;
    if (p.InTank && FindTankDriven(p.Id) is FriendlyTank ft) eye = ft.Pos + new Vector3(0, 2.7f, 0);
    else eye = p.Pos + new Vector3(0, 0.2f, 0);
    var cam = new Camera3D(eye, eye + forward, Vector3.UnitY, p.CurrentFov, CameraProjection.Perspective);
    Raylib.BeginTextureMode(rt);
    Raylib.ClearBackground(new Color((byte)35, (byte)45, (byte)65, (byte)255));
    Raylib.BeginMode3D(cam);
    DrawWorld();
    Raylib.EndMode3D();
    DrawHudFor(p, 0, 0, rtW, rtH);
    Raylib.EndTextureMode();
}

void DrawWorld()
{
    Raylib.DrawPlane(Vector3.Zero, new Vector2(ArenaSize * 2, ArenaSize * 2), new Color((byte)58, (byte)80, (byte)55, (byte)255));
    Raylib.DrawGrid(156, 2f);
    foreach (var p in platforms) DrawTerrain(p);
    foreach (var h in healthPacks)
    {
        if (!h.Active) continue;
        Raylib.DrawCube(h.Pos + new Vector3(0, 0.35f, 0), 1.2f, 0.35f, 1.2f, Color.White);
        Raylib.DrawCube(h.Pos + new Vector3(0, 0.7f, 0), 0.35f, 0.7f, 0.35f, Color.Green);
        Raylib.DrawCube(h.Pos + new Vector3(0, 0.7f, 0), 0.75f, 0.25f, 0.35f, Color.Green);
    }
    foreach (var wp in weaponPickups)
    {
        if (!wp.Active) continue;
        Color crateCol = wp.WeaponIdx switch {
            1 => Color.SkyBlue, 2 => Color.Gray, 3 => Color.Orange, 4 => Color.Magenta, 5 => Color.DarkPurple, _ => Color.White
        };
        Raylib.DrawCube(wp.Pos + new Vector3(0, 0.35f, 0), 1.1f, 0.7f, 1.1f, crateCol);
        Raylib.DrawCubeWires(wp.Pos + new Vector3(0, 0.35f, 0), 1.1f, 0.7f, 1.1f, Color.Black);
    }
    foreach (var ft in friendlyTanks)
    {
        if (!ft.Alive) continue;
        DrawFriendlyTank(ft.Pos, ft.Yaw);
        DrawHpBar3D(ft.Pos + new Vector3(0, 3.3f, 0), ft.Hp / ft.MaxHp, Color.Lime);
    }
    foreach (var a in allies)
    {
        if (!a.Alive) continue;
        Raylib.DrawCube(a.Pos + new Vector3(0, 1, 0), 0.9f, 2f, 0.9f, Color.Blue);
        Raylib.DrawSphere(a.Pos + new Vector3(0, 2.25f, 0), 0.28f, Color.SkyBlue);
        Raylib.DrawCube(a.Pos + new Vector3(0.55f, 1.25f, 0.2f), 0.75f, 0.18f, 0.18f, Color.DarkBlue);
        DrawHpBar3D(a.Pos + new Vector3(0, 2.7f, 0), a.Hp / a.MaxHp, Color.SkyBlue);
    }
    foreach (var e in enemies)
    {
        Color body = e.Type == EnemyType.Shooter ? Color.Red : e.Type == EnemyType.Brawler ? Color.Orange : new Color((byte)95, (byte)30, (byte)30, (byte)255);
        if (e.HitFlash > 0) body = Color.White;
        if (e.Type == EnemyType.Tank) DrawTank(e.Pos, body);
        else
        {
            Raylib.DrawCube(e.Pos + new Vector3(0, 1, 0), 1f, 2f, 1f, body);
            Raylib.DrawCubeWires(e.Pos + new Vector3(0, 1, 0), 1f, 2f, 1f, Color.Black);
            Raylib.DrawSphere(e.Pos + new Vector3(0, 2.3f, 0), 0.3f, body);
        }
        DrawHpBar3D(e.Center + new Vector3(0, e.Type == EnemyType.Tank ? 2.4f : 1.3f, 0), e.Hp / e.MaxHp, Color.Red);
    }
    // Player avatars (only render those who are alive AND not "us" — but we don't know "us" here)
    // For Versus mode, render both players as boxes; for Co-op, also useful so each player can see the other
    for (int i = 0; i < playerCount; i++)
    {
        var p = players[i];
        if (p.Hp <= 0 || p.InTank) continue;
        Color pc = i == 0 ? Color.SkyBlue : Color.Pink;
        Raylib.DrawCube(p.Pos + new Vector3(0, 0.85f, 0), 0.7f, 1.7f, 0.7f, pc);
        Raylib.DrawSphere(p.Pos + new Vector3(0, 1.95f, 0), 0.28f, pc);
    }
    foreach (var b in enemyBullets)
        Raylib.DrawSphere(b.Pos, b.Damage > 20 ? 0.22f : 0.12f, b.Damage > 20 ? Color.Orange : Color.Yellow);
    foreach (var r in playerRockets)
    {
        Raylib.DrawSphere(r.Pos, r.Big ? 0.55f : 0.2f, Color.Orange);
        var trailColor = r.Big ? new Color((byte)255, (byte)200, (byte)80, (byte)255) : new Color((byte)255, (byte)170, (byte)60, (byte)255);
        Raylib.DrawLine3D(r.Pos - r.Dir * 6f, r.Pos, trailColor);
    }
    foreach (var t in tracers) Raylib.DrawLine3D(t.From, t.To, t.Color);
    foreach (var h in hitMarks) Raylib.DrawSphere(h.Pos, 0.18f * (h.Life / 0.25f + 0.3f), new Color((byte)255, (byte)200, (byte)80, (byte)200));
    foreach (var ex in explosions)
    {
        float t = 1f - ex.Life / ex.MaxLife;
        float r = ex.MaxRadius * t;
        Raylib.DrawSphere(ex.Pos, r, new Color((byte)255, (byte)145, (byte)35, (byte)((1 - t) * 180)));
    }
}

void DrawTerrain(TerrainBlock t)
{
    var size = t.Box.Max - t.Box.Min;
    var center = (t.Box.Max + t.Box.Min) * 0.5f;
    Raylib.DrawCube(center, size.X, size.Y, size.Z, t.Color);
    if (size.Y > 0.3f) Raylib.DrawCubeWires(center, size.X, size.Y, size.Z, Color.Black);
}

void DrawTank(Vector3 p, Color c)
{
    Raylib.DrawCube(p + new Vector3(0, 0.9f, 0), 3.6f, 1.4f, 4.4f, c);
    Raylib.DrawCube(p + new Vector3(0, 1.85f, 0), 2.1f, 1.0f, 2.2f, c);
    Raylib.DrawCube(p + new Vector3(0, 1.95f, 2.0f), 0.45f, 0.45f, 3.3f, Color.DarkGray);
    Raylib.DrawCube(p + new Vector3(-1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, Color.Black);
    Raylib.DrawCube(p + new Vector3(1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, Color.Black);
}

void DrawFriendlyTank(Vector3 p, float yawRad)
{
    float yawDeg = yawRad * 180f / MathF.PI;
    var body = new Color((byte)60, (byte)110, (byte)65, (byte)255);
    var turret = new Color((byte)75, (byte)125, (byte)80, (byte)255);
    Rlgl.PushMatrix();
    Rlgl.Translatef(p.X, p.Y, p.Z);
    Rlgl.Rotatef(yawDeg, 0, 1, 0);
    Raylib.DrawCube(new Vector3(0, 0.9f, 0), 3.6f, 1.4f, 4.4f, body);
    Raylib.DrawCubeWires(new Vector3(0, 0.9f, 0), 3.6f, 1.4f, 4.4f, Color.Black);
    Raylib.DrawCube(new Vector3(0, 1.85f, 0), 2.1f, 1.0f, 2.2f, turret);
    Raylib.DrawCube(new Vector3(0, 1.95f, 2.0f), 0.45f, 0.45f, 3.3f, Color.DarkGray);
    Raylib.DrawCube(new Vector3(-1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, Color.Black);
    Raylib.DrawCube(new Vector3(1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, Color.Black);
    Rlgl.PopMatrix();
}

void DrawHpBar3D(Vector3 pos, float frac, Color color)
{
    frac = Math.Clamp(frac, 0, 1);
    Raylib.DrawCube(pos, 2.0f, 0.12f, 0.12f, Color.Black);
    Raylib.DrawCube(pos + new Vector3((frac - 1) * 0.5f, 0.03f, 0), 2.0f * frac, 0.12f, 0.14f, color);
}

void DrawHudFor(Player p, int x0, int y0, int rw, int rh)
{
    int cx = x0 + rw / 2;
    int cy = y0 + rh / 2;
    Raylib.DrawLine(cx - 12, cy, cx - 3, cy, Color.White);
    Raylib.DrawLine(cx + 3, cy, cx + 12, cy, Color.White);
    Raylib.DrawLine(cx, cy - 12, cx, cy - 3, Color.White);
    Raylib.DrawLine(cx, cy + 3, cx, cy + 12, Color.White);

    var w = weapons[p.Weapon];
    // Scope overlay
    if (p.RmbDown && !p.InTank && w.Sniper && p.CurrentFov < 30f)
    {
        int rad = (int)(rh * 0.3f);
        Raylib.DrawRectangle(x0, y0, rw, cy - rad - y0, Color.Black);
        Raylib.DrawRectangle(x0, cy + rad, rw, rh - (cy + rad - y0), Color.Black);
        Raylib.DrawRectangle(x0, cy - rad, cx - rad - x0, rad * 2, Color.Black);
        Raylib.DrawRectangle(cx + rad, cy - rad, rw - (cx + rad - x0), rad * 2, Color.Black);
        Raylib.DrawCircleLines(cx, cy, rad, Color.Black);
        Raylib.DrawLine(cx - rad, cy, cx + rad, cy, Color.Black);
        Raylib.DrawLine(cx, cy - rad, cx, cy + rad, Color.Black);
    }

    // Damage indicators
    foreach (var ind in p.DmgInds)
    {
        float a = ind.Angle;
        float op = MathF.Max(0, ind.Life / 1.5f);
        byte alpha = (byte)(op * 255);
        float radius = 120f;
        var dir = new Vector2(MathF.Sin(a), -MathF.Cos(a));
        var perp = new Vector2(-dir.Y, dir.X);
        var ce = new Vector2(cx, cy);
        var tip = ce + dir * radius;
        var bL = ce + dir * (radius + 50) + perp * 35;
        var bR = ce + dir * (radius + 50) - perp * 35;
        Raylib.DrawTriangle(tip, bL, bR, new Color((byte)255, (byte)50, (byte)50, alpha));
    }

    // HP bar
    Raylib.DrawRectangle(x0 + 20, y0 + rh - 70, 300, 34, new Color((byte)0, (byte)0, (byte)0, (byte)185));
    int hpW = (int)(p.Hp / PlayerMaxHp * 296);
    Raylib.DrawRectangle(x0 + 22, y0 + rh - 68, hpW, 30, p.Hp > 55 ? Color.Red : new Color((byte)255, (byte)80, (byte)80, (byte)255));
    string playerTag = playerCount == 1 ? "HP" : (p.Id == 0 ? "P1 HP" : "P2 HP");
    Raylib.DrawText($"{playerTag} {(int)p.Hp}/{(int)PlayerMaxHp}", x0 + 32, y0 + rh - 63, 22, Color.White);

    if (p.InTank && FindTankDriven(p.Id) is FriendlyTank ft)
    {
        Raylib.DrawRectangle(x0 + 20, y0 + rh - 110, 300, 30, new Color((byte)0, (byte)0, (byte)0, (byte)185));
        int tw = (int)(ft.Hp / ft.MaxHp * 296);
        Raylib.DrawRectangle(x0 + 22, y0 + rh - 108, tw, 26, Color.Lime);
        Raylib.DrawText($"TANK {(int)ft.Hp}/{(int)ft.MaxHp}", x0 + 32, y0 + rh - 105, 20, Color.Black);
    }

    // Top-left info
    if (gameMode == GameMode.CoOp)
    {
        Raylib.DrawText($"Wave {waveNum}/8", x0 + 20, y0 + 20, 26, Color.White);
        Raylib.DrawText($"Score {p.Score}", x0 + 20, y0 + 50, 24, Color.White);
        Raylib.DrawText($"Enemies {enemies.Count}", x0 + 20, y0 + 80, 20, Color.RayWhite);
    }
    else
    {
        int min = Math.Max(0, (int)versusTimer / 60);
        int sec = Math.Max(0, (int)versusTimer % 60);
        Raylib.DrawText($"VERSUS  {min}:{sec:D2}", x0 + 20, y0 + 20, 26, Color.White);
        Raylib.DrawText($"P1 {players[0].Score}   P2 {(playerCount > 1 ? players[1].Score : 0)}", x0 + 20, y0 + 50, 24, Color.White);
        Raylib.DrawText($"First to {VersusKillsToWin}", x0 + 20, y0 + 80, 18, Color.LightGray);
    }

    string label;
    if (p.InTank) label = "TANK GUN (F to exit)";
    else
    {
        string ammoTxt = w.MagSize == 0 ? "" : p.Reload > 0 ? " reloading..." : $" {p.Ammo[p.Weapon]}/{w.MagSize}";
        string ownTag = (!p.Owned[p.Weapon] && weaponPickupsMode) ? " (LOCKED)" : "";
        label = $"{p.Weapon + 1}. {w.Name}{ammoTxt}{ownTag}";
    }
    Raylib.DrawText(label, x0 + rw - Raylib.MeasureText(label, 24) - 20, y0 + rh - 50, 24, Color.White);

    // Tank entry hint
    if (!p.InTank)
    {
        foreach (var ftt in friendlyTanks)
        {
            if (!ftt.Alive || ftt.Driver != null) continue;
            if (Vector3.Distance(p.Pos, ftt.Pos) < 5f)
            {
                string hint = "Press " + p.Keys.EnterTank + " to enter tank";
                int hw = Raylib.MeasureText(hint, 24);
                Raylib.DrawRectangle(cx - hw / 2 - 10, cy + 60, hw + 20, 34, new Color((byte)0, (byte)0, (byte)0, (byte)180));
                Raylib.DrawText(hint, cx - hw / 2, cy + 66, 24, Color.Yellow);
                break;
            }
        }
    }

    // Dead overlay
    if (p.Hp <= 0)
    {
        Raylib.DrawRectangle(x0, y0, rw, rh, new Color((byte)0, (byte)0, (byte)0, (byte)120));
        string deadText = gameMode == GameMode.Versus
            ? $"DOWNED - respawn in {p.RespawnTimer:F1}s"
            : $"DOWN - reviving in {p.RespawnTimer:F1}s";
        int dw = Raylib.MeasureText(deadText, 36);
        Raylib.DrawText(deadText, cx - dw / 2, cy - 18, 36, Color.Red);
    }
}

// =========================================================================
// Types
// =========================================================================
record Weapon(string Name, int MagSize, float Damage, float Cooldown, float ReloadTime,
              float Range, bool Explosive, float Spread, bool Full, bool Melee, bool Sniper, string SfxKey);
record TerrainBlock(BoundingBox Box, Color Color);

enum AppState { StartMenu, ModeMenu, KeyConfigMenu, IdentifyMice, Playing, Paused, GameOver }
enum GameMode { CoOp, Versus }
enum SplitMode { Horizontal, Vertical }
enum EnemyType { Shooter, Brawler, Tank }

class Player
{
    public int Id;
    public Vector3 Pos;
    public float Yaw, Pitch;
    public float Hp = 180f;
    public int Weapon;
    public int[] Ammo;
    public bool[] Owned;
    public float Reload;
    public float Cooldown;
    public float SwordAnim;
    public int Score;
    public bool InTank;
    public bool RmbDown;
    public bool PrevF, PrevQ;
    public float CurrentFov = 75f;
    public List<DamageIndicator> DmgInds = new();
    public KeyBindings Keys;
    public IntPtr MouseHandle;
    public float RespawnTimer;

    public Player(int id, Weapon[] weapons, KeyBindings keys)
    {
        Id = id;
        Ammo = new int[weapons.Length];
        Owned = new bool[weapons.Length];
        for (int i = 0; i < weapons.Length; i++) { Ammo[i] = weapons[i].MagSize; Owned[i] = true; }
        Keys = keys;
    }

    public void Reset(Vector3 spawn)
    {
        Pos = spawn;
        Yaw = 0; Pitch = 0;
        Hp = 180f;
        Reload = Cooldown = SwordAnim = 0;
        InTank = false;
        DmgInds.Clear();
        CurrentFov = 75f;
        RespawnTimer = 0;
    }

    public void AddDamageIndicator(Vector3 sourcePos)
    {
        float dx = sourcePos.X - Pos.X;
        float dz = sourcePos.Z - Pos.Z;
        if (dx * dx + dz * dz < 0.04f) return;
        float worldA = MathF.Atan2(dx, dz);
        float rel = worldA - Yaw;
        while (rel > MathF.PI) rel -= 2f * MathF.PI;
        while (rel < -MathF.PI) rel += 2f * MathF.PI;
        DmgInds.Add(new DamageIndicator { Angle = rel, Life = 1.5f });
        if (DmgInds.Count > 6) DmgInds.RemoveAt(0);
    }
}

class Enemy
{
    public Vector3 Pos;
    public Vector3 Vel;
    public float Hp;
    public float MaxHp;
    public EnemyType Type;
    public float AttackCd;
    public float HitFlash;
    public float StrafeTimer;
    public bool StrafeDir;
    public float Radius = 0.55f;
    public bool Alive;
    public Vector3 Center => Pos + new Vector3(0, Type == EnemyType.Tank ? 1.3f : 1.1f, 0);
    public BoundingBox Bounds => Type == EnemyType.Tank
        ? new BoundingBox(Pos + new Vector3(-2.0f, 0, -2.4f), Pos + new Vector3(2.0f, 2.6f, 2.4f))
        : new BoundingBox(Pos + new Vector3(-0.55f, 0, -0.55f), Pos + new Vector3(0.55f, 2.35f, 0.55f));
}

class Ally
{
    public Vector3 Pos;
    public Vector3 Offset;
    public float Hp;
    public float MaxHp;
    public float FireCd;
    public float Respawn;
    public bool Alive;
    public int FollowsPlayer = 0;
}

class FriendlyTank
{
    public Vector3 Pos;
    public float Yaw;
    public float Hp = 600;
    public float MaxHp = 600;
    public bool Alive = true;
    public int? Driver;
    public float FireCd;
}

class HealthPack { public Vector3 Pos; public bool Active; public float Respawn; }
class WeaponPickup { public Vector3 Pos; public int WeaponIdx; public bool Active; public float Respawn; }
class Bullet { public Vector3 Pos; public Vector3 Dir; public float Speed; public float Damage; public float Radius; public float Life; }
class Rocket { public Vector3 Pos; public Vector3 PrevPos; public Vector3 Dir; public float Life; public float Damage; public float Radius; public bool Big; public int OwnerPlayer; }
class Tracer { public Vector3 From; public Vector3 To; public float Life; public Color Color; }
class HitMark { public Vector3 Pos; public float Life; }
class Explosion { public Vector3 Pos; public float MaxRadius; public float Life; public float MaxLife; }
class DamageIndicator { public float Angle; public float Life; }

// =========================================================================
// Key bindings
// =========================================================================
public class KeyBindings
{
    public KeyboardKey Forward { get; set; } = KeyboardKey.W;
    public KeyboardKey Back { get; set; } = KeyboardKey.S;
    public KeyboardKey Left { get; set; } = KeyboardKey.A;
    public KeyboardKey Right { get; set; } = KeyboardKey.D;
    public KeyboardKey Sprint { get; set; } = KeyboardKey.LeftShift;
    public KeyboardKey Reload { get; set; } = KeyboardKey.R;
    public KeyboardKey CycleWeapon { get; set; } = KeyboardKey.Q;
    public KeyboardKey EnterTank { get; set; } = KeyboardKey.F;
    public KeyboardKey[] Weapons { get; set; } = new[] {
        KeyboardKey.One, KeyboardKey.Two, KeyboardKey.Three,
        KeyboardKey.Four, KeyboardKey.Five, KeyboardKey.Six
    };

    public static KeyBindings Default(int playerIdx)
    {
        if (playerIdx == 0) return new KeyBindings();
        return new KeyBindings
        {
            Forward = KeyboardKey.Up,
            Back = KeyboardKey.Down,
            Left = KeyboardKey.Left,
            Right = KeyboardKey.Right,
            Sprint = KeyboardKey.RightShift,
            Reload = KeyboardKey.RightControl,
            CycleWeapon = KeyboardKey.End,
            EnterTank = KeyboardKey.RightAlt,
            Weapons = new[] {
                KeyboardKey.Kp1, KeyboardKey.Kp2, KeyboardKey.Kp3,
                KeyboardKey.Kp4, KeyboardKey.Kp5, KeyboardKey.Kp6
            }
        };
    }
}

public class KeyBindingsConfig
{
    public KeyBindings P1 { get; set; } = KeyBindings.Default(0);
    public KeyBindings P2 { get; set; } = KeyBindings.Default(1);
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

// =========================================================================
// Raw Input (Win32) — per-device mouse support for 2 mice
// =========================================================================
static class RawMouse
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterRawInputDevices(IntPtr pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    const int GWLP_WNDPROC = -4;
    const uint WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIDEV_INPUTSINK = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    public class DeviceState
    {
        int _dx, _dy;
        public bool LeftDown;
        public bool RightDown;
        public bool LeftPressedThisFrame;
        public bool JustClicked; // for identification phase
        public (int dx, int dy) ConsumeDelta()
        {
            var r = (_dx, _dy);
            _dx = 0; _dy = 0;
            return r;
        }
        public void Accumulate(int dx, int dy) { _dx += dx; _dy += dy; }
    }

    public static Dictionary<IntPtr, DeviceState> Devices = new();
    public static bool IsRegistered { get; private set; }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    static WndProcDelegate? _wndProcDelegate;
    static IntPtr _oldWndProc;
    static byte[] _buffer = new byte[256];

    public static bool Initialize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var dev = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            };
            int devSize = Marshal.SizeOf<RAWINPUTDEVICE>();
            var devPtr = Marshal.AllocHGlobal(devSize);
            try
            {
                Marshal.StructureToPtr(dev, devPtr, false);
                if (!RegisterRawInputDevices(devPtr, 1, (uint)devSize))
                    return false;
            }
            finally { Marshal.FreeHGlobal(devPtr); }
            _wndProcDelegate = MyWndProc;
            var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProc);
            if (_oldWndProc == IntPtr.Zero) return false;
            IsRegistered = true;
            return true;
        }
        catch (Exception ex) { Console.WriteLine($"RawMouse.Initialize: {ex.Message}"); return false; }
    }

    static IntPtr MyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            ProcessInput(lParam);
        }
        return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    static void ProcessInput(IntPtr lParam)
    {
        unsafe
        {
            fixed (byte* p = _buffer)
            {
                uint size = (uint)_buffer.Length;
                IntPtr bufPtr = (IntPtr)p;
                uint headerSize = 24; // RAWINPUTHEADER size on x64
                uint result = GetRawInputData(lParam, RID_INPUT, bufPtr, ref size, headerSize);
                if (result == 0 || size < 48) return;
                uint dwType = BitConverter.ToUInt32(_buffer, 0);
                if (dwType != 0) return; // RIM_TYPEMOUSE
                long hDeviceLong = BitConverter.ToInt64(_buffer, 8);
                IntPtr hDevice = new IntPtr(hDeviceLong);
                ushort usButtonFlags = BitConverter.ToUInt16(_buffer, 28);
                int lLastX = BitConverter.ToInt32(_buffer, 36);
                int lLastY = BitConverter.ToInt32(_buffer, 40);
                if (!Devices.TryGetValue(hDevice, out var dev))
                {
                    dev = new DeviceState();
                    Devices[hDevice] = dev;
                }
                dev.Accumulate(lLastX, lLastY);
                if ((usButtonFlags & 0x0001) != 0) { dev.LeftDown = true; dev.LeftPressedThisFrame = true; dev.JustClicked = true; }
                if ((usButtonFlags & 0x0002) != 0) { dev.LeftDown = false; }
                if ((usButtonFlags & 0x0004) != 0) { dev.RightDown = true; dev.JustClicked = true; }
                if ((usButtonFlags & 0x0008) != 0) { dev.RightDown = false; }
            }
        }
    }

    public static void NewFrame()
    {
        foreach (var d in Devices.Values) d.LeftPressedThisFrame = false;
    }
}

// =========================================================================
// Procedural SFX bank
// =========================================================================
class SfxBank
{
    readonly int _sr;
    readonly Dictionary<string, Sound> _sounds = new();
    readonly Random _rng = new();

    public SfxBank(int sampleRate)
    {
        _sr = sampleRate;
        Add("pistol", NoiseTone(0.07f, 0.45f, 4500, 700, 220, 88, 0.25f, true));
        Add("smg", NoiseTone(0.04f, 0.30f, 5200, 1200, 0, 0, 0, false));
        Add("mg", NoiseTone(0.05f, 0.45f, 3200, 600, 140, 56, 0.18f, true));
        Add("rpg", NoiseTone(0.45f, 0.55f, 1100, 80, 120, 42, 0.5f, false));
        Add("sniper", NoiseTone(0.12f, 0.8f, 2500, 400, 160, 48, 0.4f, true));
        Add("sword", NoiseTone(0.18f, 0.4f, 7000, 1500, 0, 0, 0, false));
        Add("tankShell", NoiseTone(0.5f, 0.75f, 700, 50, 80, 24, 0.6f, false));
        Add("explode", NoiseTone(0.6f, 0.85f, 700, 40, 60, 18, 0.5f, false));
        Add("hit", Tone(0.04f, 700, 350, 0.15f, true));
        Add("enemyHurt", Combine(Tone(0.05f, 280, 112, 0.18f, true), NoiseTone(0.04f, 0.12f, 3000, 200, 0, 0, 0, false)));
        Add("playerHurt", Combine(Tone(0.18f, 140, 42, 0.35f, true), NoiseTone(0.12f, 0.25f, 800, 100, 0, 0, 0, false)));
        Add("pickup", Combine(Tone(0.08f, 700, 700, 0.4f, false), Pad(0.07f), Tone(0.12f, 1050, 1050, 0.4f, false)));
        Add("reload", Combine(Tone(0.04f, 220, 220, 0.2f, false, true), Pad(0.09f), Tone(0.05f, 160, 160, 0.18f, false, true)));
        Add("empty", Tone(0.04f, 200, 200, 0.18f, false, true));
        Add("waveStart", Combine(Tone(0.16f, 220, 220, 0.4f, false, true), Pad(0.14f), Tone(0.2f, 330, 330, 0.4f, false, true)));
        Add("waveClear", Combine(Tone(0.22f, 660, 660, 0.4f, false), Pad(0.16f), Tone(0.3f, 440, 440, 0.4f, false)));
        Add("death", Combine(Tone(0.3f, 220, 44, 0.5f, true), Pad(0.22f), Tone(0.5f, 110, 33, 0.4f, true)));
        Add("victory", Combine(Tone(0.22f, 660, 660, 0.4f, false), Pad(0.17f), Tone(0.22f, 880, 880, 0.4f, false), Pad(0.17f), Tone(0.22f, 1100, 1100, 0.4f, false), Pad(0.17f), Tone(0.3f, 1760, 1760, 0.4f, false)));
    }

    void Add(string name, short[] samples)
    {
        var wave = WaveFromSamples(samples, _sr);
        var sound = Raylib.LoadSoundFromWave(wave);
        Raylib.UnloadWave(wave);
        _sounds[name] = sound;
    }

    public void Play(string name)
    {
        if (_sounds.TryGetValue(name, out var s)) Raylib.PlaySound(s);
    }

    public void Unload()
    {
        foreach (var s in _sounds.Values) Raylib.UnloadSound(s);
        _sounds.Clear();
    }

    short[] Pad(float seconds) => new short[(int)(_sr * seconds)];

    short[] Combine(params short[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var data = new short[total];
        int off = 0;
        foreach (var p in parts) { Array.Copy(p, 0, data, off, p.Length); off += p.Length; }
        return data;
    }

    short[] NoiseTone(float duration, float vol, float lowpass, float highpass,
                     float toneStartFreq, float toneEndFreq, float toneVol, bool square)
    {
        int n = (int)(_sr * duration);
        var data = new short[n];
        float lpAlpha = lowpass > 0 ? 1f - MathF.Exp(-2f * MathF.PI * lowpass / _sr) : 1f;
        float hpAlpha = highpass > 0 ? MathF.Exp(-2f * MathF.PI * highpass / _sr) : 0f;
        float low = 0, prevIn = 0, hpOut = 0;
        float tonePhase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float env = MathF.Min(1f, i / (_sr * 0.003f)) * MathF.Exp(-t * 6f);
            float s = (float)(_rng.NextDouble() * 2 - 1);
            if (lowpass > 0) { low = low + lpAlpha * (s - low); s = low; }
            if (highpass > 0) { hpOut = hpAlpha * (hpOut + s - prevIn); prevIn = s; s = hpOut; }
            float sample = s * vol;
            if (toneVol > 0)
            {
                float freq = toneStartFreq * MathF.Pow(toneEndFreq / toneStartFreq, t);
                tonePhase += 2f * MathF.PI * freq / _sr;
                float ts = square ? (MathF.Sin(tonePhase) > 0 ? 1f : -1f) : MathF.Sin(tonePhase);
                sample += ts * toneVol * env;
            }
            sample *= env;
            sample = Math.Clamp(sample, -1f, 1f);
            data[i] = (short)(sample * short.MaxValue * 0.85f);
        }
        return data;
    }

    short[] Tone(float duration, float startFreq, float endFreq, float vol, bool sawtooth, bool square = false)
    {
        int n = (int)(_sr * duration);
        var data = new short[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float env = MathF.Min(1f, i / (_sr * 0.005f)) * MathF.Exp(-t * 5f);
            float freq = startFreq * MathF.Pow(endFreq / startFreq, t);
            phase += 2f * MathF.PI * freq / _sr;
            float sample;
            if (square) sample = MathF.Sin(phase) > 0 ? 1f : -1f;
            else if (sawtooth) sample = 2f * ((phase / (2f * MathF.PI)) - MathF.Floor(0.5f + phase / (2f * MathF.PI)));
            else sample = MathF.Sin(phase);
            sample *= vol * env;
            sample = Math.Clamp(sample, -1f, 1f);
            data[i] = (short)(sample * short.MaxValue * 0.85f);
        }
        return data;
    }

    static Wave WaveFromSamples(short[] samples, int sampleRate)
    {
        int dataSize = samples.Length * 2;
        var bytes = new byte[44 + dataSize];
        bytes[0] = (byte)'R'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F'; bytes[3] = (byte)'F';
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        bytes[8] = (byte)'W'; bytes[9] = (byte)'A'; bytes[10] = (byte)'V'; bytes[11] = (byte)'E';
        bytes[12] = (byte)'f'; bytes[13] = (byte)'m'; bytes[14] = (byte)'t'; bytes[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(bytes, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        bytes[36] = (byte)'d'; bytes[37] = (byte)'a'; bytes[38] = (byte)'t'; bytes[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        Buffer.BlockCopy(samples, 0, bytes, 44, dataSize);
        return Raylib.LoadWaveFromMemory(".wav", bytes);
    }
}
