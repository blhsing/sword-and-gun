// Sword & Gun (刀劍與槍) — desktop edition.
//
// A native .NET 9 + raylib (Raylib-cs) port of the web game in game.html; the two are
// kept at feature parity. Single-player waves, same-PC split-screen 1v1 (two pointing
// devices via Raw Input, incl. precision touchpads), and LAN 1v1 over UDP. Features:
// pistol-only start with weapon-crate pickups, climbable multi-tier buildings + jumping,
// a drivable tank per player, health packs, damage-direction indicators, per-player
// names, score/time win conditions, and a directional "sun" lighting shader.
// See README.md for build/run instructions and full controls.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Raylib_cs;

const int ScreenW = 1280;
const int ScreenH = 720;
const float Arena = 78f;
const float PlayerMaxHp = 180f;
const float PlayerRadius = 0.45f;
const int DiscoveryPort = 45678;
const int GamePort = 45679;
const float MaxHp = 180f;
const float StepHeight = 0.55f;   // obstacles this tall or shorter are walked/stepped over
const float JumpVel = 11f;
const float Gravity = 24f;
const float EyeHeight = 1.7f;     // player Pos.Y when standing on the ground (feet at Y=0)
const float TankCollideR = 2.6f;
bool rawInputAvailable = false;

Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);  // 4x MSAA smooths grid/edges
Raylib.InitWindow(ScreenW, ScreenH, "刀劍與槍 - 區網版");
Raylib.SetTargetFPS(60);
Raylib.SetExitKey(KeyboardKey.Null);
try
{
    string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
    if (File.Exists(iconPath)) { Image icon = Raylib.LoadImage(iconPath); Raylib.SetWindowIcon(icon); Raylib.UnloadImage(icon); }
}
catch { }
Raylib.ToggleBorderlessWindowed();   // start in borderless full-screen
try
{
    IntPtr hwnd = GetWindowHandleSafe();
    if (hwnd != IntPtr.Zero) rawInputAvailable = RawMouse.Initialize(hwnd);
}
catch (Exception ex)
{
    Console.WriteLine($"Raw input init failed: {ex.Message}");
}

var uiCodepoints = new HashSet<int>();
bool uiFontCustom = false;
// Pick the first usable CJK font. Prefer heavy gothic faces (微軟正黑體) which stay
// legible at small sizes; the thin brush face 標楷體 is only a last resort. .ttc
// collections are unpacked to a single-face .ttf in memory (raylib can't read .ttc).
byte[]? uiFontData = null;
string? uiFontPath = null;
foreach (var c in new[] {
    @"C:\Windows\Fonts\msjhbd.ttc",  // 微軟正黑體 粗體
    @"C:\Windows\Fonts\msjh.ttc",    // 微軟正黑體
    @"C:\Windows\Fonts\msyhbd.ttc",  // 微軟雅黑 粗體
    @"C:\Windows\Fonts\msyh.ttc",
    @"C:\Windows\Fonts\simhei.ttf",  // 黑体
    @"C:\Windows\Fonts\kaiu.ttf",    // 標楷體（細，最後備援）
    @"C:\Windows\Fonts\mingliu.ttc",
    @"C:\Windows\Fonts\simsun.ttc",
})
{
    if (!File.Exists(c)) continue;
    var data = LoadUiFontData(c);
    if (data != null) { uiFontPath = c; uiFontData = data; break; }
}
// Two atlases: a small one for HUD text (~20-34px) and a large one for titles
// (~44-72px). DrawTextC picks whichever is closest to the requested size, so text
// is drawn near 1:1 and thin CJK strokes are never lost to heavy minification.
const int UiFontSmallBase = 32;
const int UiFontBigBase = 72;
AddCodepoints("刀劍與槍玩家名字請輸入可貼上繁體中文單人波次同機雙人分割對戰兩套鍵盤支滑鼠建立區網房間廣播搜尋並加入已啟用辨識多不可用開始選擇返回等待對手連線本機正在玩家會自動看到的有人後按尚未找到確認電腦在個且防火牆允許請點擊自己的左鍵偵測裝置方向鍵主區網對戰分數敵重生勝利失敗暫停繼續輸入名字坦克進入下車移動指向觸控板設定先動確是兩不同依序指提示右");
Font uiFontSmall = LoadChineseFont(UiFontSmallBase);
Font uiFontBig = LoadChineseFont(UiFontBigBase);
Shader lightShader = LoadLightShader();   // directional "sun" shading for all 3D geometry
var rng = new Random();

var weapons = new Weapon[]
{
    new("手槍", 16, 24, 0.22f, 75, false, 0.012f, false, false),
    new("衝鋒槍", 40, 13, 0.055f, 58, false, 0.035f, true, false),
    new("機關槍", 90, 18, 0.085f, 85, false, 0.028f, true, false),
    new("火箭筒", 5, 115, 0.9f, 100, true, 0, false, false),
    new("刀劍", 0, 280, 0.35f, 4, false, 0, false, true),
    new("狙擊槍", 5, 180, 1.0f, 220, false, 0, false, false),
    new("拳頭", 0, 30, 0.4f, 2.2f, false, 0, false, true),   // always-owned melee fallback
};
const int SniperIndex = 5;
const int FistsIndex = 6;
const float BaseFov = 75f;
string[] WeaponEn = { "Pistol", "SMG", "MG", "RPG", "Sword", "Sniper", "Fists" };
foreach (var wp in weapons) EnsureFontForText(wp.Name);   // bake weapon names so the HUD bar can show them

AppState state = AppState.Start;
GameMode mode = GameMode.Single;
string playerName = "";
string player2Name = "";
int splitNameActive = 0;   // which name field is being edited on the split-name screen
string statusText = "輸入名字，選擇模式";
int menuIndex = 0;
bool quitRequested = false;
bool english = false;   // UI language: false = 繁體中文 (default), true = English (toggle F10)
string langFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwordAndGun.lang");
try { if (File.Exists(langFile)) english = File.ReadAllText(langFile).Trim() == "en"; } catch { }   // persisted choice
string scoreFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwordAndGun.scores");
var rankings = new List<ScoreEntry>();
bool scoreRecorded = false;
LoadRankings();
float bannerTimer = 0;
string bannerText = "";

Player player = new(weapons);
Player player2 = new(weapons);
RemotePlayer remote = new();
Tank playerTank = new();    // local player's drivable tank (P1 in two-player modes)
Tank player2Tank = new();   // P2's tank in split / the remote peer's tank in LAN
Ally ally = new();          // friendly AI trooper (single-player)
Color FriendlyTankBody = new(70, 110, 90, 255);
Color FriendlyTankTurret = new(95, 150, 115, 255);
Color EnemyTankBody = new(120, 60, 60, 255);
Color EnemyTankTurret = new(155, 85, 85, 255);
Color PlayerColorP1 = new(52, 108, 220, 255);     // P1 / host body — blue
Color PlayerColorP1Head = new(150, 190, 255, 255);
Color PlayerColorP2 = new(225, 110, 45, 255);     // P2 / client body — orange
Color PlayerColorP2Head = new(255, 185, 120, 255);
var enemies = new List<Enemy>();
var obstacles = new List<Obstacle>();
var bullets = new List<Bullet>();
var rockets = new List<Rocket>();
var tracers = new List<Tracer>();
var screenTracers = new List<ScreenTracer>();
var dmgIndP1 = new List<DamageInd>();   // damage-direction arrows for player / P1
var dmgIndP2 = new List<DamageInd>();   // ... for P2 in split-screen
var flashes = new List<Flash>();        // brief muzzle-flash sprites
const float Rad2Deg = 180f / MathF.PI;
var pickups = new List<Pickup>();
var weaponCrates = new List<WeaponCrate>();
RenderTexture2D splitTexL = default, splitTexR = default;
int splitTexW = 0, splitTexH = 0;
int wave = 1;
int score = 0;
bool gameOver = false;
bool won = false;
float waveDelay = 0;

UdpClient? discoveryUdp = null;
UdpClient? gameUdp = null;
IPEndPoint? peerEndPoint = null;
DateTime lastBroadcast = DateTime.MinValue;
DateTime lastStateSent = DateTime.MinValue;
DateTime lastPeerSeen = DateTime.MinValue;
var foundHosts = new List<HostInfo>();
int selectedHost = 0;
int localKills = 0;
int remoteKills = 0;
float respawnTimer = 0;
int p1Kills = 0;
int p2Kills = 0;
WinCondition winCond = WinCondition.Score;   // 2-player end condition
int winScore = 10;                            // first to this many kills (Score mode)
float timeLimitSec = 180;                     // match length (Time mode)
float matchTimer = 0;                         // counts down while playing (Time mode)
IntPtr p1Mouse = IntPtr.Zero;
IntPtr p2Mouse = IntPtr.Zero;
int localMouseStage = 0;

KeyboardKey[] P1WeaponKeys = { KeyboardKey.One, KeyboardKey.Two, KeyboardKey.Three, KeyboardKey.Four, KeyboardKey.Five, KeyboardKey.Six };
// P2 has no numpad on a laptop, so weapons 1-6 map to the right of the number row.
KeyboardKey[] P2WeaponKeys = { KeyboardKey.Seven, KeyboardKey.Eight, KeyboardKey.Nine, KeyboardKey.Zero, KeyboardKey.Minus, KeyboardKey.Equal };
string[] P1WeaponLabels = { "1", "2", "3", "4", "5", "6", "" };    // fists has no number key (cycle/auto only)
string[] P2WeaponLabels = { "7", "8", "9", "0", "-", "=", "" };

BuildWorld();
ResetRound(false);

while (!Raylib.WindowShouldClose() && !quitRequested)
{
    float dt = Math.Min(Raylib.GetFrameTime(), 0.05f);
    if (Raylib.IsKeyPressed(KeyboardKey.F11)) Raylib.ToggleBorderlessWindowed();
    if (Raylib.IsKeyPressed(KeyboardKey.F10)) { english = !english; try { File.WriteAllText(langFile, english ? "en" : "zh"); } catch { } }   // toggle + persist 中文 / English
    // Quit from anywhere: LeftCtrl+Q. (Only LeftCtrl — RightCtrl is P2's jump, and P1's
    // weapon-swap is Q, so RightCtrl+Q would otherwise quit during split-screen play.)
    if (Raylib.IsKeyDown(KeyboardKey.LeftControl) && Raylib.IsKeyPressed(KeyboardKey.Q)) quitRequested = true;

    switch (state)
    {
        case AppState.Start:
            UpdateStart();
            break;
        case AppState.SplitNames:
            UpdateSplitNames();
            break;
        case AppState.HostLobby:
            UpdateHostLobby();
            break;
        case AppState.JoinLobby:
            UpdateJoinLobby();
            break;
        case AppState.LocalMouseSelect:
            UpdateLocalMouseSelect();
            break;
        case AppState.Playing:
            UpdateGame(dt);
            break;
        case AppState.Paused:
            UpdatePaused();
            break;
        case AppState.GameOver:
            UpdateGameOver();
            break;
    }

    Draw();
}

CloseNetwork();
Raylib.CloseWindow();

void UpdateStart()
{
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { quitRequested = true; return; }
    ReadNameInput();
    // No digit shortcuts here: number keys are captured by the name field, so use
    // arrows + Enter to choose a mode (otherwise typing a digit also switched modes).
    if (Raylib.IsKeyPressed(KeyboardKey.Down)) menuIndex = Math.Min(3, menuIndex + 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Up)) menuIndex = Math.Max(0, menuIndex - 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Enter))
    {
        playerName = CleanName(playerName);
        if (menuIndex == 0) StartSingle();
        else if (menuIndex == 1) { splitNameActive = 0; state = AppState.SplitNames; }  // enter both names first
        else if (menuIndex == 2) StartHostLobby();
        else StartJoinLobby();
    }
}

void UpdateSplitNames()
{
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { state = AppState.Start; return; }
    if (Raylib.IsKeyPressed(KeyboardKey.Tab)) splitNameActive = (splitNameActive + 1) % 3;
    if (splitNameActive == 0) playerName = EditName(playerName);
    else if (splitNameActive == 1) player2Name = EditName(player2Name);
    else
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.Right))
            winCond = winCond == WinCondition.Score ? WinCondition.Time : WinCondition.Score;
        if (winCond == WinCondition.Score)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up)) winScore = Math.Min(50, winScore + 1);
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) winScore = Math.Max(1, winScore - 1);
        }
        else
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up)) timeLimitSec = Math.Min(900, timeLimitSec + 30);
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) timeLimitSec = Math.Max(30, timeLimitSec - 30);
        }
    }
    if (Raylib.IsKeyPressed(KeyboardKey.Enter))
    {
        playerName = string.IsNullOrWhiteSpace(playerName) ? "玩家1" : CleanName(playerName);
        player2Name = string.IsNullOrWhiteSpace(player2Name) ? "玩家2" : CleanName(player2Name);
        StartLocalSplit();
    }
}

void UpdateHostLobby()
{
    PumpGameNetwork();
    BroadcastHost();
    if (peerEndPoint != null && Raylib.IsKeyPressed(KeyboardKey.Enter)) StartLanGame(true);
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { CloseNetwork(); state = AppState.Start; statusText = "已取消房間"; }
}

void UpdateJoinLobby()
{
    PumpDiscovery();
    PumpGameNetwork();
    if (Raylib.IsKeyPressed(KeyboardKey.Down)) selectedHost = Math.Min(Math.Max(0, foundHosts.Count - 1), selectedHost + 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Up)) selectedHost = Math.Max(0, selectedHost - 1);
    if (Raylib.IsKeyPressed(KeyboardKey.Enter) && foundHosts.Count > 0) JoinSelectedHost();
    if (peerEndPoint != null && Raylib.IsKeyPressed(KeyboardKey.Space)) StartLanGame(false);
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { CloseNetwork(); state = AppState.Start; statusText = "已取消搜尋"; }
}

void UpdatePaused()
{
    if (Raylib.IsKeyPressed(KeyboardKey.F2)) { RestartCurrent(); return; }
    if (Raylib.IsKeyPressed(KeyboardKey.M)) { ReturnToMenu(); return; }
    if (Raylib.IsKeyPressed(KeyboardKey.P) || Raylib.IsKeyPressed(KeyboardKey.Escape))
    {
        Raylib.DisableCursor();
        state = AppState.Playing;
    }
}

void UpdateGameOver()
{
    if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsMouseButtonPressed(MouseButton.Left))
    {
        if (mode == GameMode.LanHost) StartHostLobby();
        else if (mode == GameMode.LanClient) StartJoinLobby();
        else state = AppState.Start;
    }
}

void StartSingle()
{
    mode = GameMode.Single;
    CloseNetwork();
    ResetRound(true);
    Raylib.DisableCursor();
    state = AppState.Playing;
    statusText = "";
}

void StartLocalSplit()
{
    mode = GameMode.LocalSplit;
    CloseNetwork();
    ResetRound(false);
    enemies.Clear();
    bullets.Clear();
    rockets.Clear();
    tracers.Clear();
    p1Kills = 0;
    p2Kills = 0;
    matchTimer = timeLimitSec;
    player.Reset(weapons);
    player2.Reset(weapons);
    player.Pos = new Vector3(-18, 1.7f, 0);
    player.Yaw = MathF.PI / 2;
    player2.Pos = new Vector3(18, 1.7f, 0);
    player2.Yaw = -MathF.PI / 2;
    // One drivable tank per player, parked beside each spawn.
    playerTank.Reset(new Vector3(-18, 0, 9), 0);
    player2Tank.Reset(new Vector3(18, 0, 9), 0);
    p1Mouse = IntPtr.Zero;
    p2Mouse = IntPtr.Zero;
    localMouseStage = 0;
    RawMouse.BeginSelect();
    statusText = rawInputAvailable ? "請 P1 晃動或點擊自己的滑鼠" : "Raw Input 不可用：P2 滑鼠可能無法作用";
    if (rawInputAvailable) state = AppState.LocalMouseSelect;
    else
    {
        Raylib.DisableCursor();
        state = AppState.Playing;
    }
}

void UpdateLocalMouseSelect()
{
    // A device is picked by clicking/tapping OR by giving it a deliberate wiggle — movement
    // is the most reliable Raw Input signal, so it works even on mice whose button events
    // arrive on a HID collection that doesn't surface as a standard mouse click.
    const long Wiggle = 240;
    foreach (var kv in RawMouse.Devices)
    {
        if (!kv.Value.JustClicked && kv.Value.SelMove <= Wiggle) continue;
        if (localMouseStage == 0)
        {
            p1Mouse = kv.Key;
            localMouseStage = 1;
            statusText = "請 P2 晃動或點擊另一支滑鼠";
            RawMouse.BeginSelect();
            return;
        }
        if (RawMouse.SameGroup(kv.Key, p1Mouse))
        {
            statusText = "這是 P1 的滑鼠，請 P2 改用另一支滑鼠";
            kv.Value.JustClicked = false;
            kv.Value.SelMove = 0;
            return;
        }
        p2Mouse = kv.Key;
        foreach (var dev in RawMouse.Devices.Values) dev.JustClicked = false;
        Raylib.DisableCursor();
        state = AppState.Playing;
        statusText = "同機雙人模式";
        return;
    }
    RawMouse.NewFrame();
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) state = AppState.Start;
}

void StartHostLobby()
{
    mode = GameMode.LanHost;
    CloseNetwork();
    gameUdp = new UdpClient(GamePort);
    gameUdp.Client.Blocking = false;
    discoveryUdp = new UdpClient();
    discoveryUdp.EnableBroadcast = true;
    peerEndPoint = null;
    statusText = $"等待對手連線，本機正在廣播 UDP {DiscoveryPort}";
    state = AppState.HostLobby;
}

void StartJoinLobby()
{
    mode = GameMode.LanClient;
    CloseNetwork();
    discoveryUdp = new UdpClient(DiscoveryPort);
    discoveryUdp.EnableBroadcast = true;
    discoveryUdp.Client.Blocking = false;
    gameUdp = new UdpClient(0);
    gameUdp.Client.Blocking = false;
    foundHosts.Clear();
    selectedHost = 0;
    statusText = "正在區網搜尋房間";
    state = AppState.JoinLobby;
}

void StartLanGame(bool host)
{
    if (peerEndPoint == null) return;
    mode = host ? GameMode.LanHost : GameMode.LanClient;
    ResetRound(true);
    enemies.Clear();
    bullets.Clear();
    rockets.Clear();
    localKills = 0;
    remoteKills = 0;
    respawnTimer = 0;
    matchTimer = timeLimitSec;
    remote.Alive = false;
    player.Pos = host ? new Vector3(-18, 1.7f, 0) : new Vector3(18, 1.7f, 0);
    player.Yaw = host ? MathF.PI / 2 : -MathF.PI / 2;
    // Each peer drives its own tank near its spawn; the opponent's tank is synced.
    playerTank.Reset(host ? new Vector3(-18, 0, 8) : new Vector3(18, 0, 8), 0);
    player2Tank.Reset(host ? new Vector3(18, 0, 8) : new Vector3(-18, 0, 8), 0);
    Raylib.DisableCursor();
    SendNet($"START|{playerName}");
    state = AppState.Playing;
}

void ResetRound(bool spawnEnemies)
{
    player.Reset(weapons);
    playerName = CleanName(playerName);
    score = 0;
    wave = 1;
    waveDelay = 0;
    gameOver = false;
    won = false;
    scoreRecorded = false;
    bullets.Clear();
    rockets.Clear();
    enemies.Clear();
    pickups.Clear();
    weaponCrates.Clear();
    BuildWorld();   // fresh cover each round (tanks/rockets destroy destructibles)
    // Default single-player layout: one drivable tank for the player.
    playerTank.Reset(new Vector3(0, 0, 9), 0);
    player2Tank.Reset(new Vector3(0, 0, 9), 0);
    player2Tank.Alive = false;
    ally.Pos = new Vector3(-4, 0, -4); ally.Hp = ally.MaxHp; ally.Alive = true; ally.FireCd = 0; ally.Respawn = 0;
    SpawnPickups();
    if (spawnEnemies) SpawnWave(1);
}

// Leave the current game and go back to the main menu (M).
void ReturnToMenu()
{
    CloseNetwork();
    Raylib.EnableCursor();
    gameOver = false;
    state = AppState.Start;
}

// Restart the current mode mid-game (F2) without going back to the menu.
void RestartCurrent()
{
    dmgIndP1.Clear();
    dmgIndP2.Clear();
    if (mode == GameMode.Single) { StartSingle(); return; }
    if (mode == GameMode.LocalSplit)
    {
        ResetRound(false);
        p1Kills = 0; p2Kills = 0;
        matchTimer = timeLimitSec;
        player.Reset(weapons); player2.Reset(weapons);
        player.Pos = new Vector3(-18, 1.7f, 0); player.Yaw = MathF.PI / 2;
        player2.Pos = new Vector3(18, 1.7f, 0); player2.Yaw = -MathF.PI / 2;
        playerTank.Reset(new Vector3(-18, 0, 9), 0);
        player2Tank.Reset(new Vector3(18, 0, 9), 0);
        Raylib.DisableCursor();
        state = AppState.Playing;
        return;
    }
    // LAN: reset only the local side (the peer keeps its own state).
    ResetRound(true);
    enemies.Clear();
    localKills = 0; remoteKills = 0; respawnTimer = 0; matchTimer = timeLimitSec;
    bool host = mode == GameMode.LanHost;
    player.Pos = host ? new Vector3(-18, 1.7f, 0) : new Vector3(18, 1.7f, 0);
    player.Yaw = host ? MathF.PI / 2 : -MathF.PI / 2;
    playerTank.Reset(host ? new Vector3(-18, 0, 8) : new Vector3(18, 0, 8), 0);
    player2Tank.Reset(host ? new Vector3(18, 0, 8) : new Vector3(-18, 0, 8), 0);
    Raylib.DisableCursor();
    state = AppState.Playing;
}

void UpdateGame(float dt)
{
    if (Raylib.IsKeyPressed(KeyboardKey.F2)) { RestartCurrent(); return; }
    if (Raylib.IsKeyPressed(KeyboardKey.M)) { ReturnToMenu(); return; }
    if (Raylib.IsKeyPressed(KeyboardKey.P) || Raylib.IsKeyPressed(KeyboardKey.Escape))
    {
        Raylib.EnableCursor();
        state = AppState.Paused;
        return;
    }

    if (mode == GameMode.LocalSplit)
    {
        UpdateLocalSplit(dt);
        UpdateTracers(dt);
        if (bannerTimer > 0) bannerTimer -= dt;
        if (gameOver) { Raylib.EnableCursor(); state = AppState.GameOver; }
        return;
    }

    if (mode != GameMode.Single) PumpGameNetwork();
    UpdatePlayer(dt);
    UpdateProjectiles(dt);
    UpdateTracers(dt);
    UpdatePickups(dt);

    if (mode == GameMode.Single)
    {
        UpdateEnemies(dt);
        UpdateAlly(dt);
        enemies.RemoveAll(e => !e.Alive);
        if (enemies.Count == 0 && waveDelay <= 0)
        {
            if (wave >= 8) { won = true; gameOver = true; }
            else { waveDelay = 3f; ShowBanner(T($"第 {wave} 波完成！", $"Wave {wave} clear!"), 2); }   // 3s breather before the next wave
        }
        if (waveDelay > 0)
        {
            waveDelay -= dt;
            if (waveDelay <= 0) SpawnWave(++wave);
        }
        if (player.Hp <= 0) { player.Hp = 0; gameOver = true; won = false; }
    }
    else
    {
        UpdateLanDuel(dt);
    }

    if (bannerTimer > 0) bannerTimer -= dt;
    if (gameOver)
    {
        if (mode == GameMode.Single && !scoreRecorded) { RecordScore(playerName, score, wave, won); scoreRecorded = true; }
        Raylib.EnableCursor();
        state = AppState.GameOver;
    }
}

void UpdatePlayer(float dt)
{
    if (respawnTimer > 0)
    {
        respawnTimer -= dt;
        if (respawnTimer <= 0)
        {
            player.Hp = PlayerMaxHp;
            player.Pos = RandomSpawn();      // respawn somewhere random
            player.Yaw = YawToCenter(player.Pos);
            player.InTank = false;
        }
        SendState();
        return;
    }

    var inp = GatherSingleInput();
    var fi = UpdateHuman(player, playerTank, inp, dt, 0, true);
    if (fi.Fire) FireWeaponFor(player, fi.Eye, fi.Dir, fi.Right, 0);
    if (mode != GameMode.Single) SendState();
}

// ---------- Shared per-human-player logic (look, tank, movement, weapons) ----------

PlayerInput GatherSingleInput()
{
    Vector2 md = Raylib.GetMouseDelta();
    return BuildInput(md.X, md.Y,
        Raylib.IsMouseButtonDown(MouseButton.Left), Raylib.IsMouseButtonPressed(MouseButton.Left), Raylib.IsMouseButtonDown(MouseButton.Right),
        KeyboardKey.W, KeyboardKey.S, KeyboardKey.A, KeyboardKey.D, KeyboardKey.LeftShift,
        KeyboardKey.Space, KeyboardKey.F, KeyboardKey.R, KeyboardKey.Q, P1WeaponKeys);
}

PlayerInput GatherSplitInput(bool p2)
{
    float mdx = 0, mdy = 0;
    bool fireDown = false, firePressed = false, aimDown = false;
    IntPtr mouse = p2 ? p2Mouse : p1Mouse;
    if (mouse != IntPtr.Zero && RawMouse.TryReadGroup(mouse, out int dx, out int dy, out bool ld, out bool lp, out bool rd))
    {
        mdx = dx; mdy = dy;
        fireDown = ld;
        firePressed = lp;
        aimDown = rd;
    }
    else if (!p2)
    {
        Vector2 md = Raylib.GetMouseDelta();
        mdx = md.X; mdy = md.Y;
        fireDown = Raylib.IsMouseButtonDown(MouseButton.Left);
        firePressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
        aimDown = Raylib.IsMouseButtonDown(MouseButton.Right);
    }
    return p2
        ? BuildInput(mdx, mdy, fireDown, firePressed, aimDown,
            KeyboardKey.Up, KeyboardKey.Down, KeyboardKey.Left, KeyboardKey.Right, KeyboardKey.RightShift,
            KeyboardKey.RightControl, KeyboardKey.RightAlt, KeyboardKey.Backspace, KeyboardKey.End, P2WeaponKeys)
        : BuildInput(mdx, mdy, fireDown, firePressed, aimDown,
            KeyboardKey.W, KeyboardKey.S, KeyboardKey.A, KeyboardKey.D, KeyboardKey.LeftShift,
            KeyboardKey.Space, KeyboardKey.F, KeyboardKey.R, KeyboardKey.Q, P1WeaponKeys);
}

PlayerInput BuildInput(float ldx, float ldy, bool fire, bool firePressed, bool aim,
    KeyboardKey fwd, KeyboardKey back, KeyboardKey left, KeyboardKey right, KeyboardKey sprint,
    KeyboardKey jump, KeyboardKey interact, KeyboardKey reload, KeyboardKey next, KeyboardKey[] wsel)
{
    int sel = -1;
    for (int i = 0; i < wsel.Length; i++) if (Raylib.IsKeyPressed(wsel[i])) sel = i;
    return new PlayerInput
    {
        LookDX = ldx, LookDY = ldy,
        Fwd = Raylib.IsKeyDown(fwd), Back = Raylib.IsKeyDown(back),
        MoveLeft = Raylib.IsKeyDown(left), MoveRight = Raylib.IsKeyDown(right),
        Sprint = Raylib.IsKeyDown(sprint), Jump = Raylib.IsKeyDown(jump),
        Interact = Raylib.IsKeyDown(interact), Aim = aim,
        Fire = fire, FirePressed = firePressed,
        Reload = Raylib.IsKeyPressed(reload), NextWeapon = Raylib.IsKeyPressed(next),
        WeaponSelect = sel,
    };
}

FireIntent UpdateHuman(Player p, Tank tank, in PlayerInput inp, float dt, int ownerId, bool canDrive)
{
    // Aim-down-sights zoom: right mouse narrows the FOV (sniper hard-zooms to 18, other
    // guns a mild 50). Mouse look slows proportionally so the zoomed view stays steady.
    float targetFov = inp.Aim && !p.InTank ? (p.Weapon == SniperIndex ? 18f : 50f) : BaseFov;
    p.Fov += (targetFov - p.Fov) * Math.Min(1f, dt * 14f);
    if (MathF.Abs(p.Fov - targetFov) < 0.2f) p.Fov = targetFov;
    float sens = p.Fov / BaseFov;

    if (p.InTank && tank.Alive)
    {
        tank.Yaw -= inp.LookDX * 0.0022f * sens;
        p.Pitch = Math.Clamp(p.Pitch - inp.LookDY * 0.0022f * sens, -1.0f, 1.0f);
        p.Yaw = tank.Yaw;
    }
    else
    {
        p.Yaw -= inp.LookDX * 0.0028f * sens;
        p.Pitch = Math.Clamp(p.Pitch - inp.LookDY * 0.0028f * sens, -1.35f, 1.35f);
    }
    Vector3 fwd = Vector3.Normalize(new(MathF.Cos(p.Pitch) * MathF.Sin(p.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(p.Yaw)));
    Vector3 flat = Vector3.Normalize(new Vector3(MathF.Sin(p.Yaw), 0, MathF.Cos(p.Yaw)));
    Vector3 rightV = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));

    // Enter / exit the tank with the interact key
    if (inp.Interact && !p.PrevF)
    {
        if (!p.InTank && canDrive && tank.Alive && Vector3.Distance(p.Pos, tank.Pos) < 5f)
        {
            p.InTank = true;
            p.Yaw = tank.Yaw;
        }
        else if (p.InTank)
        {
            p.InTank = false;
            Vector3 side = new(MathF.Sin(tank.Yaw + MathF.PI / 2), 0, MathF.Cos(tank.Yaw + MathF.PI / 2));
            p.Pos = tank.Pos + side * 4f;
            p.Pos.Y = EyeHeight;
            p.VelY = 0; p.OnGround = true;
        }
    }
    p.PrevF = inp.Interact;

    if (p.InTank && tank.Alive)
    {
        float turn = (inp.MoveLeft ? 1f : 0f) - (inp.MoveRight ? 1f : 0f);
        tank.Yaw += turn * 1.2f * dt;
        Vector3 tFwd = new(MathF.Sin(tank.Yaw), 0, MathF.Cos(tank.Yaw));
        float drive = (inp.Fwd ? 1f : 0f) - (inp.Back ? 0.55f : 0f);
        MoveTank(tank, tFwd.X * drive * 7.5f * dt, tFwd.Z * drive * 7.5f * dt);
        if (tank.SmashCd > 0) tank.SmashCd -= dt;
        p.Pos = tank.Pos; p.Pos.Y = tank.Pos.Y + EyeHeight;
        p.Yaw = tank.Yaw;
        if (tank.FireCd > 0) tank.FireCd -= dt;
        if (inp.FirePressed && tank.FireCd <= 0)
        {
            tank.FireCd = 1.1f;
            Vector3 sdir = Vector3.Normalize(new(MathF.Cos(p.Pitch) * MathF.Sin(tank.Yaw), MathF.Sin(p.Pitch), MathF.Cos(p.Pitch) * MathF.Cos(tank.Yaw)));
            Vector3 muzzle = tank.Pos + new Vector3(0, 2.0f, 0) + sdir * 3.2f;
            rockets.Add(new Rocket { Pos = muzzle, Dir = sdir, Life = 5, Damage = 220, Radius = 10, Owner = ownerId, FromTank = true });
            AddFlash(muzzle);
        }
        return default;
    }

    Vector3 move = Vector3.Zero;
    if (inp.Fwd) move += flat;
    if (inp.Back) move -= flat;
    if (inp.MoveRight) move += rightV;
    if (inp.MoveLeft) move -= rightV;
    if (move.LengthSquared() > 0.001f) move = Vector3.Normalize(move);
    float speed = inp.Sprint ? 9.5f : 6.2f;
    float feetY = p.Pos.Y - EyeHeight;
    MoveCircle2D(ref p.Pos, move.X * speed * dt, move.Z * speed * dt, PlayerRadius, feetY);
    PushOutOfTank(ref p.Pos, PlayerRadius, playerTank);
    PushOutOfTank(ref p.Pos, PlayerRadius, player2Tank);
    ApplyVertical(p, inp.Jump, dt);

    if (inp.WeaponSelect >= 0 && inp.WeaponSelect < weapons.Length && p.Owned[inp.WeaponSelect]) p.Weapon = inp.WeaponSelect;
    if (inp.NextWeapon)
        for (int s = 1; s <= weapons.Length; s++) { int nw = (p.Weapon + s) % weapons.Length; if (p.Owned[nw]) { p.Weapon = nw; break; } }
    if (inp.Reload && weapons[p.Weapon].MagSize > 0) p.Ammo[p.Weapon] = weapons[p.Weapon].MagSize;
    if (p.Cooldown > 0) p.Cooldown -= dt;
    if (p.SwordAnim > 0) p.SwordAnim -= dt;

    Weapon w = weapons[p.Weapon];
    bool firing = w.FullAuto ? inp.Fire : inp.FirePressed;
    return new FireIntent { Fire = firing && p.Cooldown <= 0, Eye = p.Pos + new Vector3(0, 0.2f, 0), Dir = fwd, Right = rightV };
}

void FireWeaponFor(Player p, Vector3 eye, Vector3 dir, Vector3 rightV, int ownerId)
{
    Weapon w = weapons[p.Weapon];
    p.Cooldown = w.Cooldown;
    if (w.Melee)
    {
        p.SwordAnim = 0.3f;
        if (mode == GameMode.Single)
        {
            foreach (var e in enemies)
            {
                Vector3 to = e.Center - eye;
                float d = to.Length();
                if (d < w.Range && Vector3.Dot(Vector3.Normalize(to), dir) > 0.52f) DamageEnemy(e, w.Damage);
            }
        }
        else if (remote.Alive)
        {
            Vector3 to = PlayerCenter(remote.Pos) - eye;
            float d = to.Length();
            if (d < w.Range && Vector3.Dot(Vector3.Normalize(to), dir) > 0.52f) SendHit(w.Damage, eye);
        }
        return;
    }
    if (w.MagSize > 0)
    {
        if (p.Ammo[p.Weapon] <= 0) return;
        p.Ammo[p.Weapon]--;
        AutoSwitchIfEmpty(p);
    }
    Vector3 shotDir = ApplySpread(dir, w.Spread);
    Vector3 muzzle = MuzzlePoint(p.Pos, p.Yaw, p.Pitch);
    AddFlash(muzzle);
    if (w.Explosive)
    {
        rockets.Add(new Rocket { Pos = eye + shotDir * 0.8f, Dir = shotDir, Life = 5, Damage = w.Damage, Radius = 7.5f, Owner = ownerId });
        return;
    }
    var hit = Hitscan(eye, shotDir, w.Range);
    if (hit.Enemy != null) DamageEnemy(hit.Enemy, w.Damage);
    if (hit.Remote) SendHit(w.Damage, hit.Point);
    if (mode != GameMode.Single && player2Tank.Alive)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, shotDir), TankBounds(player2Tank));
        if (rc.Hit && rc.Distance <= w.Range && rc.Distance < Vector3.Distance(eye, hit.Point)) SendTankHit(w.Damage);
    }
    tracers.Add(new Tracer { From = muzzle, To = hit.Point, Life = 0.06f, Color = w.Name == "狙擊槍" ? Color.White : Color.Yellow });
}

void UpdateEnemies(float dt)
{
    foreach (var e in enemies)
    {
        if (!e.Alive) continue;
        e.AttackCd -= dt;
        // Target the nearer of the player and the friendly ally.
        bool atAlly = ally.Alive && Vector3.DistanceSquared(e.Pos, ally.Pos) < Vector3.DistanceSquared(e.Pos, player.Pos);
        Vector3 tgtPos = atAlly ? ally.Pos : player.Pos;
        Vector3 to = tgtPos - e.Pos;
        to.Y = 0;
        float d = to.Length();
        Vector3 dn = d > 0.01f ? to / d : Vector3.Zero;
        if (e.Type == EnemyType.Shooter)
        {
            if (d > 18) MoveEnemy(e, SteerAround(e.Pos, dn, e.Radius) * 2.6f * dt);
            else if (d < 10) MoveEnemy(e, SteerAround(e.Pos, -dn, e.Radius) * 2.0f * dt);
            if (e.AttackCd <= 0 && d < 45)
            {
                e.AttackCd = 1.1f;
                bullets.Add(new Bullet { Pos = e.Pos + new Vector3(0, 1.1f, 0), Dir = Vector3.Normalize(tgtPos + new Vector3(0, .5f, 0) - (e.Pos + new Vector3(0, 1.1f, 0))), Speed = 26, Damage = 12, Life = 5 });
            }
        }
        else if (e.Type == EnemyType.Brawler)
        {
            if (d > 1.8f) MoveEnemy(e, SteerAround(e.Pos, dn, e.Radius) * 5.0f * dt);
            if (d < 2.2f && e.AttackCd <= 0)
            {
                e.AttackCd = 0.85f;
                if (atAlly) DamageAlly(18, e.Pos);
                else if (player.InTank && playerTank.Alive) { playerTank.Hp -= 18; ShowDamageFrom(dmgIndP1, e.Pos, playerTank.Pos); if (playerTank.Hp <= 0) EjectAndDestroyTank(playerTank, player); }
                else { player.Hp -= 18; ShowDamageFrom(dmgIndP1, e.Pos, player.Pos); }
            }
        }
        else
        {
            if (d > 28) MoveEnemy(e, SteerAround(e.Pos, dn, e.Radius) * 1.3f * dt);
            if (e.AttackCd <= 0 && d < 65)
            {
                e.AttackCd = 2.4f;
                bullets.Add(new Bullet { Pos = e.Pos + new Vector3(0, 2, 0), Dir = Vector3.Normalize(tgtPos + new Vector3(0, .4f, 0) - (e.Pos + new Vector3(0, 2, 0))), Speed = 18, Damage = 38, Life = 5 });
            }
        }
        e.Pos.X = Math.Clamp(e.Pos.X, -Arena + 2, Arena - 2);
        e.Pos.Z = Math.Clamp(e.Pos.Z, -Arena + 2, Arena - 2);
        // Shove enemies out of the tank so they can't ride the hull/barrel; ramming hurts them.
        bool rammed = PushOutOfTank(ref e.Pos, e.Radius, playerTank);
        PushOutOfTank(ref e.Pos, e.Radius, player2Tank);
        if (rammed && player.InTank) DamageEnemy(e, 130f * dt);
    }
}

void DamageAlly(float dmg, Vector3 source)
{
    if (!ally.Alive) return;
    ally.Hp -= dmg;
    if (ally.Hp <= 0) { ally.Alive = false; ally.Respawn = 8f; }
}

// Friendly AI trooper: follows the player, auto-fires at the nearest enemy in sight.
void UpdateAlly(float dt)
{
    if (!ally.Alive)
    {
        ally.Respawn -= dt;
        if (ally.Respawn <= 0) { ally.Hp = ally.MaxHp; ally.Pos = new Vector3(player.Pos.X + (Rnd() - 0.5f) * 8, 0, player.Pos.Z + (Rnd() - 0.5f) * 8); ally.Alive = true; }
        return;
    }
    if (ally.FireCd > 0) ally.FireCd -= dt;
    Enemy? target = null; float bd = float.MaxValue;
    foreach (var e in enemies) { if (!e.Alive) continue; float dd = Vector3.DistanceSquared(ally.Pos, e.Pos); if (dd < bd) { bd = dd; target = e; } }
    float dist = MathF.Sqrt(bd);
    Vector3 desired = new(player.Pos.X - 4, 0, player.Pos.Z - 4);   // follow offset
    if (target != null && dist < 30) desired = ally.Pos;            // hold position to fire
    Vector3 toDesired = new(desired.X - ally.Pos.X, 0, desired.Z - ally.Pos.Z);
    if (toDesired.Length() > 3)
    {
        Vector3 m = Vector3.Normalize(toDesired) * 3.8f * dt;
        MoveCircle(ref ally.Pos, new Vector3(m.X, 0, 0), 0.45f);
        MoveCircle(ref ally.Pos, new Vector3(0, 0, m.Z), 0.45f);
    }
    ally.Pos.Y = 0;
    Vector3 lookAt = target != null ? target.Center : player.Pos;
    Vector3 lookDir = new(lookAt.X - ally.Pos.X, 0, lookAt.Z - ally.Pos.Z);
    if (lookDir.LengthSquared() > 0.001f) ally.Yaw = MathF.Atan2(lookDir.X, lookDir.Z);
    if (target != null && ally.FireCd <= 0 && dist < 45)
    {
        Vector3 from = ally.Pos + new Vector3(0, 1.25f, 0);
        if (HasLineOfSight(from, target.Center))
        {
            ally.FireCd = 0.55f;
            DamageEnemy(target, 6);
            tracers.Add(new Tracer { From = from, To = target.Center, Life = 0.06f, Color = Color.SkyBlue });
        }
    }
}

bool HasLineOfSight(Vector3 from, Vector3 to)
{
    Vector3 dir = to - from;
    float dist = dir.Length();
    if (dist < 0.01f) return true;
    dir /= dist;
    foreach (var o in obstacles)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(from, dir), o.Box);
        if (rc.Hit && rc.Distance < dist - 0.15f) return false;
    }
    return true;
}

void UpdateProjectiles(float dt)
{
    for (int i = bullets.Count - 1; i >= 0; i--)
    {
        var b = bullets[i];
        b.Pos += b.Dir * b.Speed * dt;
        b.Life -= dt;
        // Enemy bullets (single/LAN) hit the local player, or their tank if driving.
        if (player.InTank && playerTank.Alive && Vector3.Distance(b.Pos, playerTank.Pos + new Vector3(0, 1.2f, 0)) < 2.2f)
        {
            playerTank.Hp -= b.Damage;
            ShowDamageFrom(dmgIndP1, b.Pos, playerTank.Pos);
            if (playerTank.Hp <= 0) EjectAndDestroyTank(playerTank, player);
            bullets.RemoveAt(i);
        }
        else if (!player.InTank && Vector3.Distance(b.Pos, player.Pos + new Vector3(0, .7f, 0)) < 0.75f)
        {
            player.Hp -= b.Damage;
            ShowDamageFrom(dmgIndP1, b.Pos, player.Pos);
            bullets.RemoveAt(i);
        }
        else if (ally.Alive && mode == GameMode.Single && Vector3.Distance(b.Pos, ally.Pos + new Vector3(0, 1f, 0)) < 0.7f)
        {
            DamageAlly(b.Damage, b.Pos);
            bullets.RemoveAt(i);
        }
        else if (b.Life <= 0 || PointInObstacle(b.Pos)) bullets.RemoveAt(i);
    }
    for (int i = rockets.Count - 1; i >= 0; i--)
    {
        var r = rockets[i];
        r.Pos += r.Dir * (r.FromTank ? 48f : 31f) * dt;
        r.Life -= dt;
        bool explode = r.Life <= 0 || PointInObstacle(r.Pos) || Math.Abs(r.Pos.X) > Arena || Math.Abs(r.Pos.Z) > Arena;
        if (mode == GameMode.LocalSplit)
        {
            Player tp = r.Owner == 1 ? player : player2;
            Tank tt = r.Owner == 1 ? playerTank : player2Tank;
            if (!tp.InTank && tp.Hp > 0 && Vector3.Distance(tp.Pos, r.Pos) < 1.6f) explode = true;
            if (tt.Alive && Vector3.Distance(tt.Pos + new Vector3(0, 1.2f, 0), r.Pos) < 2.6f) explode = true;
        }
        else
        {
            foreach (var e in enemies) if (e.Alive && Vector3.Distance(e.Pos, r.Pos) < 1.6f) explode = true;
            if (mode != GameMode.Single && remote.Alive && Vector3.Distance(remote.Pos, r.Pos) < 1.6f) explode = true;
            if (mode != GameMode.Single && player2Tank.Alive && Vector3.Distance(player2Tank.Pos + new Vector3(0, 1.2f, 0), r.Pos) < 2.6f) explode = true;
        }
        if (explode)
        {
            if (mode == GameMode.LocalSplit)
            {
                Player tp = r.Owner == 1 ? player : player2;
                Tank tt = r.Owner == 1 ? playerTank : player2Tank;
                if (!tp.InTank && tp.Hp > 0)
                {
                    float d = Vector3.Distance(PlayerCenter(tp.Pos), r.Pos);
                    if (d <= r.Radius) DamageSplitPlayer(tp, r.Damage * (1 - d / r.Radius * 0.55f), r.Owner, r.Pos);
                }
                if (tt.Alive)
                {
                    float d = Vector3.Distance(tt.Pos + new Vector3(0, 1.2f, 0), r.Pos);
                    if (d <= r.Radius) DamageSplitTank(tt, tp, r.Damage * (1 - d / r.Radius * 0.55f), r.Owner, r.Pos);
                }
            }
            else
            {
                foreach (var e in enemies)
                {
                    float d = Vector3.Distance(e.Center, r.Pos);
                    if (d <= r.Radius) DamageEnemy(e, r.Damage * (1 - d / r.Radius * 0.55f));
                }
                if (mode != GameMode.Single && remote.Alive)
                {
                    float d = Vector3.Distance(remote.Pos, r.Pos);
                    if (d <= r.Radius) SendHit(r.Damage * (1 - d / r.Radius * 0.55f), r.Pos);
                }
                if (mode != GameMode.Single && player2Tank.Alive)
                {
                    float d = Vector3.Distance(player2Tank.Pos + new Vector3(0, 1.2f, 0), r.Pos);
                    if (d <= r.Radius) SendTankHit(r.Damage * (1 - d / r.Radius * 0.55f));
                }
            }
            rockets.RemoveAt(i);
        }
    }
}

void UpdateTracers(float dt)
{
    for (int i = tracers.Count - 1; i >= 0; i--)
    {
        tracers[i].Life -= dt;
        if (tracers[i].Life <= 0) tracers.RemoveAt(i);
    }
    for (int i = screenTracers.Count - 1; i >= 0; i--)
    {
        screenTracers[i].Life -= dt;
        if (screenTracers[i].Life <= 0) screenTracers.RemoveAt(i);
    }
    for (int i = dmgIndP1.Count - 1; i >= 0; i--) { dmgIndP1[i].Life -= dt; if (dmgIndP1[i].Life <= 0) dmgIndP1.RemoveAt(i); }
    for (int i = dmgIndP2.Count - 1; i >= 0; i--) { dmgIndP2[i].Life -= dt; if (dmgIndP2[i].Life <= 0) dmgIndP2.RemoveAt(i); }
    for (int i = flashes.Count - 1; i >= 0; i--) { flashes[i].Life -= dt; if (flashes[i].Life <= 0) flashes.RemoveAt(i); }
}

// Geometry helpers shared by avatars and view-models.
Vector3 FlatFwd(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));
Vector3 RightOf(float yaw) => Vector3.Normalize(Vector3.Cross(FlatFwd(yaw), Vector3.UnitY));
Vector3 AimVec(float yaw, float pitch) => Vector3.Normalize(new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));
void AddFlash(Vector3 pos) => flashes.Add(new Flash { Pos = pos, Life = 0.05f });

// Record a hit arriving from worldspace `source`, shown as a red arrow around the
// crosshair pointing toward where the damage came from (see DrawDamageArrows).
void ShowDamageFrom(List<DamageInd> list, Vector3 source, Vector3 refPos)
{
    float dx = source.X - refPos.X, dz = source.Z - refPos.Z;
    if (dx * dx + dz * dz < 0.04f) return;
    list.Add(new DamageInd { WorldAngle = MathF.Atan2(dx, dz), Life = 1.5f });
    if (list.Count > 6) list.RemoveAt(0);
}

void UpdatePickups(float dt) => UpdatePickupsFor(dt, player);

void UpdatePickupsFor(float dt, params Player[] players)
{
    foreach (var p in pickups)
    {
        if (!p.Active) { p.Respawn -= dt; if (p.Respawn <= 0) p.Active = true; continue; }
        foreach (var pl in players)
        {
            if (pl.Hp <= 0 || Vector3.Distance(pl.Pos, p.Pos) >= 2) continue;
            Tank tk = TankOf(pl);
            if (pl.InTank && tk.Alive && tk.Hp < tk.MaxHp)        // heal the tank you're driving
            {
                tk.Hp = Math.Min(tk.MaxHp, tk.Hp + 120);
                p.Active = false; p.Respawn = 18; break;
            }
            if (!pl.InTank && pl.Hp < MaxHp)                      // heal on foot
            {
                pl.Hp = Math.Min(MaxHp, pl.Hp + 55);
                p.Active = false; p.Respawn = 18; break;
            }
        }
    }
    foreach (var c in weaponCrates)
    {
        if (!c.Active) { c.Respawn -= dt; if (c.Respawn <= 0) c.Active = true; continue; }
        foreach (var pl in players)
        {
            if (pl.Hp > 0 && Vector3.Distance(pl.Pos, c.Pos) < 2)
            {
                bool wasOwned = pl.Owned[c.Weapon];
                pl.Owned[c.Weapon] = true;
                pl.Ammo[c.Weapon] = weapons[c.Weapon].MagSize;
                if (!wasOwned) pl.Weapon = c.Weapon;
                c.Active = false;
                c.Respawn = 75;   // weapon crates come back only after a long while
                break;
            }
        }
    }
}

void UpdateLanDuel(float dt)
{
    if (winCond == WinCondition.Score)
    {
        if (localKills >= winScore || remoteKills >= winScore) { won = localKills >= remoteKills; gameOver = true; }
    }
    else
    {
        matchTimer -= dt;
        if (matchTimer <= 0) { matchTimer = 0; won = localKills >= remoteKills; gameOver = true; }
    }
}

void UpdateLocalSplit(float dt)
{
    UpdateSplitPlayer(player, player2, playerTank, player2Tank, false, dt);
    UpdateSplitPlayer(player2, player, player2Tank, playerTank, true, dt);
    UpdateProjectiles(dt);
    UpdatePickupsFor(dt, player, player2);
    RawMouse.NewFrame();
    if (winCond == WinCondition.Score)
    {
        if (p1Kills >= winScore || p2Kills >= winScore) gameOver = true;
    }
    else
    {
        matchTimer -= dt;
        if (matchTimer <= 0) { matchTimer = 0; gameOver = true; }
    }
}

void UpdateSplitPlayer(Player p, Player target, Tank ownTank, Tank targetTank, bool p2, float dt)
{
    if (p.HitFlash > 0) p.HitFlash -= dt;
    if (p.Hp <= 0)
    {
        p.Cooldown -= dt;
        if (p.Cooldown <= -3f)
        {
            p.Hp = MaxHp;
            p.Pos = RandomSpawn();          // respawn somewhere random
            p.Yaw = YawToCenter(p.Pos);
            p.Pitch = 0;
            p.HitFlash = 0;
            p.InTank = false;
        }
        return;
    }

    var inp = GatherSplitInput(p2);
    int id = p2 ? 1 : 0;
    var fi = UpdateHuman(p, ownTank, inp, dt, id, true);
    if (fi.Fire) FireSplitWeapon(p, target, targetTank, fi.Eye, fi.Dir, id);
}

void FireSplitWeapon(Player shooter, Player target, Tank targetTank, Vector3 eye, Vector3 dir, int shooterId)
{
    Weapon w = weapons[shooter.Weapon];
    shooter.Cooldown = w.Cooldown;
    bool isP2 = shooterId == 1;
    if (w.Melee)
    {
        shooter.SwordAnim = 0.3f;
        if (!target.InTank && target.Hp > 0)
        {
            Vector3 to = PlayerCenter(target.Pos) - eye;
            if (to.Length() < w.Range && Vector3.Dot(Vector3.Normalize(to), dir) > 0.52f) DamageSplitPlayer(target, w.Damage, shooterId, eye);
        }
        return;
    }
    if (w.MagSize > 0)
    {
        if (shooter.Ammo[shooter.Weapon] <= 0) return;
        shooter.Ammo[shooter.Weapon]--;
        AutoSwitchIfEmpty(shooter);
    }
    Vector3 shotDir = ApplySpread(dir, w.Spread);
    AddFlash(MuzzlePoint(shooter.Pos, shooter.Yaw, shooter.Pitch));
    if (w.Explosive)
    {
        rockets.Add(new Rocket { Pos = eye + shotDir * 0.8f, Dir = shotDir, Life = 5, Damage = w.Damage, Radius = 7.5f, Owner = shooterId });
        return;
    }
    // Raycast against the opponent (when on foot), the opponent's tank, and cover.
    float best = w.Range;
    int hitKind = 0; // 1 = player, 2 = tank
    Vector3 end = eye + shotDir * w.Range;
    if (!target.InTank && target.Hp > 0)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, shotDir), PlayerBounds(target));
        if (rc.Hit && rc.Distance <= best) { best = rc.Distance; end = rc.Point; hitKind = 1; }
    }
    if (targetTank.Alive)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, shotDir), TankBounds(targetTank));
        if (rc.Hit && rc.Distance <= best) { best = rc.Distance; end = rc.Point; hitKind = 2; }
    }
    foreach (var o in obstacles)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(eye, shotDir), o.Box);
        if (rc.Hit && rc.Distance > 0 && rc.Distance < best) { best = rc.Distance; end = rc.Point; hitKind = 0; }
    }
    if (hitKind == 1) DamageSplitPlayer(target, w.Damage, shooterId, eye);
    else if (hitKind == 2) DamageSplitTank(targetTank, target, w.Damage, shooterId, eye);
    tracers.Add(new Tracer { From = MuzzlePoint(shooter.Pos, shooter.Yaw, shooter.Pitch), To = end, Life = 0.06f, Color = isP2 ? Color.SkyBlue : Color.Yellow });
    screenTracers.Add(new ScreenTracer { RightSide = isP2, Life = 0.09f, Color = isP2 ? Color.SkyBlue : Color.Yellow });
}

void DamageSplitPlayer(Player target, float damage, int attackerId, Vector3 source)
{
    target.Hp = Math.Max(0, target.Hp - damage);
    target.HitFlash = 0.14f;
    ShowDamageFrom(DmgListFor(target), source, target.Pos);
    if (target.Hp <= 0)
    {
        target.Cooldown = -0.001f;
        if (attackerId == 1) p2Kills++;
        else p1Kills++;
    }
}

void DamageSplitTank(Tank t, Player owner, float damage, int attackerId, Vector3 source)
{
    t.Hp -= damage;
    if (owner.InTank) ShowDamageFrom(DmgListFor(owner), source, t.Pos);
    if (t.Hp <= 0) EjectAndDestroyTank(t, owner);
}

List<DamageInd> DmgListFor(Player p) => p == player2 ? dmgIndP2 : dmgIndP1;
Tank TankOf(Player p) => p == player2 ? player2Tank : playerTank;

// When the current firearm just ran dry, swap to the next owned weapon that still has
// ammo (melee/infinite weapons always count).
void AutoSwitchIfEmpty(Player p)
{
    if (weapons[p.Weapon].MagSize <= 0 || p.Ammo[p.Weapon] > 0) return;
    for (int s = 1; s <= weapons.Length; s++)
    {
        int nw = (p.Weapon + s) % weapons.Length;
        if (p.Owned[nw] && (weapons[nw].MagSize <= 0 || p.Ammo[nw] > 0)) { p.Weapon = nw; return; }
    }
}

void EjectAndDestroyTank(Tank t, Player owner)
{
    t.Alive = false;
    if (owner.InTank)
    {
        owner.InTank = false;
        owner.Hp = Math.Max(1, owner.Hp - 25);
        owner.Pos = t.Pos + new Vector3(3, EyeHeight, 0);
        owner.VelY = 0;
        owner.OnGround = true;
    }
}

void DamageEnemy(Enemy e, float dmg)
{
    e.Hp -= dmg;
    e.HitFlash = 0.12f;
    if (e.Hp <= 0)
    {
        e.Alive = false;
        score += e.Type == EnemyType.Tank ? 550 : e.Type == EnemyType.Brawler ? 150 : 100;
    }
}

HitResult Hitscan(Vector3 origin, Vector3 dir, float range)
{
    Enemy? bestEnemy = null;
    bool remoteHit = false;
    Vector3 hitPoint = origin + dir * range;
    float best = range;
    foreach (var e in enemies)
    {
        if (!e.Alive) continue;
        var rc = Raylib.GetRayCollisionBox(new Ray(origin, dir), e.Bounds);
        if (rc.Hit && rc.Distance < best) { best = rc.Distance; bestEnemy = e; remoteHit = false; hitPoint = rc.Point; }
    }
    if (mode != GameMode.Single && remote.Alive)
    {
        var bb = RemotePlayerBounds(remote);
        var rc = Raylib.GetRayCollisionBox(new Ray(origin, dir), bb);
        if (rc.Hit && rc.Distance < best) { best = rc.Distance; bestEnemy = null; remoteHit = true; hitPoint = rc.Point; }
    }
    foreach (var o in obstacles)
    {
        var rc = Raylib.GetRayCollisionBox(new Ray(origin, dir), o.Box);
        if (rc.Hit && rc.Distance < best) { best = rc.Distance; bestEnemy = null; remoteHit = false; hitPoint = rc.Point; }
    }
    return new HitResult(bestEnemy, remoteHit, hitPoint);
}

void BuildWorld()
{
    obstacles.Clear();
    AddBox(new(0, 2, -Arena), Arena * 2, 4, 1, false);
    AddBox(new(0, 2, Arena), Arena * 2, 4, 1, false);
    AddBox(new(-Arena, 2, 0), 1, 4, Arena * 2, false);
    AddBox(new(Arena, 2, 0), 1, 4, Arena * 2, false);
    // Climbable multi-tier buildings: step up the stairs, jump between tiers.
    foreach (var (cx, cz) in new[] { (42f, 28f), (-42f, 28f), (42f, -28f), (-42f, -28f), (0f, 50f), (0f, -50f) })
    {
        AddBox(new(cx, 0.25f, cz - 4), 3, 0.5f, 1.5f, false);      // stair step (top 0.5)
        AddBox(new(cx, 0.75f, cz - 2.5f), 3, 1.5f, 1.5f, false);   // stair block (top 1.5)
        AddBox(new(cx, 0.75f, cz), 6, 1.5f, 6, false);             // tier 1 (top 1.5)
        AddBox(new(cx + 1, 2.0f, cz + 1), 4, 1.0f, 4, false);      // tier 2 (top 2.5)
        AddBox(new(cx + 2, 3.0f, cz + 2), 3, 1.0f, 3, false);      // tier 3 roof (top 3.5)
    }
    for (int i = 0; i < 40; i++)
    {
        var p = RandSpot(10, 1);
        AddBox(new(p.X, 1.2f, p.Y), 2 + Rnd() * 5, 2.4f, 2 + Rnd() * 5, true);
    }
    for (int i = 0; i < 18; i++)
    {
        var p = RandSpot(15, 1);
        bool x = Rnd() < .5f;
        AddBox(new(p.X, .55f, p.Y), x ? 9 + Rnd() * 8 : 1.2f, 1.1f, x ? 1.2f : 9 + Rnd() * 8, true);
    }
    // Keep every spawn point (and the tanks beside them) clear of cover, or a player
    // can spawn trapped inside a box — unable to move and with shots blocked at the muzzle.
    ClearSpawnArea(new(0, 0, 0), new(-18, 0, 0), new(18, 0, 0),
                   new(0, 0, 9), new(-18, 0, 9), new(18, 0, 9), new(-18, 0, 8), new(18, 0, 8));
}

void ClearSpawnArea(params Vector3[] spots)
{
    obstacles.RemoveAll(o =>
    {
        if (!o.Destructible || o.Box.Max.Y <= StepHeight) return false;
        foreach (var s in spots)
            if (s.X > o.Box.Min.X - 3f && s.X < o.Box.Max.X + 3f && s.Z > o.Box.Min.Z - 3f && s.Z < o.Box.Max.Z + 3f)
                return true;
        return false;
    });
}

void AddBox(Vector3 c, float sx, float sy, float sz, bool destructible)
{
    var b = new BoundingBox(c - new Vector3(sx / 2, sy / 2, sz / 2), c + new Vector3(sx / 2, sy / 2, sz / 2));
    obstacles.Add(new Obstacle(b, destructible));
}

void SpawnWave(int n)
{
    ShowBanner(T($"第 {n} 波", $"Wave {n}"), 2);
    int shooters = 3 + n, brawlers = n, tanks = Math.Max(0, (n - 1) / 2);
    for (int i = 0; i < shooters; i++) SpawnEnemy(EnemyType.Shooter, 60 + n * 8);
    for (int i = 0; i < brawlers; i++) SpawnEnemy(EnemyType.Brawler, 90 + n * 10);
    for (int i = 0; i < tanks; i++) SpawnEnemy(EnemyType.Tank, 400 + n * 55);
}

void SpawnEnemy(EnemyType type, float hp)
{
    var p = RandSpot(20, type == EnemyType.Tank ? 2.5f : 1);
    enemies.Add(new Enemy { Pos = new Vector3(p.X, 0, p.Y), Type = type, Hp = hp, MaxHp = hp, Alive = true, AttackCd = 1 + Rnd() });
}

void SpawnPickups()
{
    for (int i = 0; i < 12; i++)
    {
        var p = RandSpot(8, 0);
        pickups.Add(new Pickup { Pos = new Vector3(p.X, 0, p.Y), Active = true });
    }
    for (int i = 0; i < 10; i++)
    {
        var p = RandSpot(8, 0);
        weaponCrates.Add(new WeaponCrate { Pos = new Vector3(p.X, 0, p.Y), Weapon = 1 + i % 5, Active = true });
    }
}

void MoveEnemy(Enemy e, Vector3 delta)
{
    MoveCircle(ref e.Pos, new Vector3(delta.X, 0, 0), e.Radius);
    MoveCircle(ref e.Pos, new Vector3(0, 0, delta.Z), e.Radius);
}

Vector3 RotateY(Vector3 v, float a)
{
    float c = MathF.Cos(a), s = MathF.Sin(a);
    return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
}

// Steer a desired horizontal direction around obstacles: probe a fan of angles and
// pick the one closest to the goal whose short look-ahead isn't blocked, so enemies
// walk around cover instead of grinding into it.
Vector3 SteerAround(Vector3 pos, Vector3 desired, float radius)
{
    if (desired.LengthSquared() < 1e-4f) return desired;
    desired = Vector3.Normalize(new Vector3(desired.X, 0, desired.Z));
    ReadOnlySpan<float> offs = stackalloc float[] { 0, 22, -22, 45, -45, 70, -70, 100, -100, 135, -135 };
    foreach (float deg in offs)
    {
        Vector3 d = RotateY(desired, deg * MathF.PI / 180f);
        if (!BlockedAhead(pos, d, radius)) return d;
    }
    return desired;
}

bool BlockedAhead(Vector3 pos, Vector3 dir, float radius)
{
    Vector3 probe = pos + dir * (radius + 1.6f);
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (b.Max.Y <= StepHeight) continue;   // low cover can be stepped over
        if (probe.X > b.Min.X - radius && probe.X < b.Max.X + radius && probe.Z > b.Min.Z - radius && probe.Z < b.Max.Z + radius) return true;
    }
    return false;
}

void MoveCircle(ref Vector3 pos, Vector3 delta, float radius)
{
    Vector3 np = pos + delta;
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (np.X > b.Min.X - radius && np.X < b.Max.X + radius && np.Z > b.Min.Z - radius && np.Z < b.Max.Z + radius) return;
    }
    pos = np;
    pos.X = Math.Clamp(pos.X, -Arena + 1, Arena - 1);
    pos.Z = Math.Clamp(pos.Z, -Arena + 1, Arena - 1);
}

// Axis-separated horizontal movement with step-up: obstacles whose top is within
// StepHeight of the player's feet are walked over; taller ones block.
void MoveCircle2D(ref Vector3 pos, float dx, float dz, float radius, float feetY)
{
    float thresh = feetY + StepHeight + 0.01f;
    float nx = pos.X + dx, nz = pos.Z;
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (nx > b.Min.X - radius && nx < b.Max.X + radius && nz > b.Min.Z - radius && nz < b.Max.Z + radius)
        {
            if (b.Max.Y <= thresh) continue;
            nx = pos.X; break;
        }
    }
    float nz2 = pos.Z + dz;
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (nx > b.Min.X - radius && nx < b.Max.X + radius && nz2 > b.Min.Z - radius && nz2 < b.Max.Z + radius)
        {
            if (b.Max.Y <= thresh) continue;
            nz2 = pos.Z; break;
        }
    }
    pos.X = Math.Clamp(nx, -Arena + 1, Arena - 1);
    pos.Z = Math.Clamp(nz2, -Arena + 1, Arena - 1);
}

// Highest walkable surface directly under (x,z) whose top isn't above maxAllowedTop.
float HighestGroundUnder(float x, float z, float maxAllowedTop)
{
    float g = 0;
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (x > b.Min.X && x < b.Max.X && z > b.Min.Z && z < b.Max.Z)
            if (b.Max.Y > g && b.Max.Y <= maxAllowedTop + 0.01f) g = b.Max.Y;
    }
    return g;
}

// Gravity / jumping / landing on building tiers. jumpHeld is the jump key state this frame.
void ApplyVertical(Player p, bool jumpHeld, float dt)
{
    float feetY = p.Pos.Y - EyeHeight;
    float groundY = HighestGroundUnder(p.Pos.X, p.Pos.Z, feetY + StepHeight);
    if (p.OnGround)
    {
        if (groundY < feetY - 0.1f) p.OnGround = false;
        else
        {
            p.Pos.Y = groundY + EyeHeight;
            if (jumpHeld && !p.PrevJump) { p.VelY = JumpVel; p.OnGround = false; }
        }
    }
    p.PrevJump = jumpHeld;
    if (!p.OnGround)
    {
        p.VelY -= Gravity * dt;
        float nextFeet = (p.Pos.Y - EyeHeight) + p.VelY * dt;
        if (p.VelY <= 0 && nextFeet <= groundY) { nextFeet = groundY; p.VelY = 0; p.OnGround = true; }
        p.Pos.Y = nextFeet + EyeHeight;
    }
}

// Shove a point out of a tank's footprint (so players can't walk through a tank).
bool PushOutOfTank(ref Vector3 pos, float radius, Tank t)
{
    if (!t.Alive) return false;
    float dx = pos.X - t.Pos.X, dz = pos.Z - t.Pos.Z;
    float d = MathF.Sqrt(dx * dx + dz * dz);
    float minD = TankCollideR + radius;
    if (d >= minD) return false;
    float nx = d < 0.0001f ? 1 : dx / d;
    float nz = d < 0.0001f ? 0 : dz / d;
    pos.X = t.Pos.X + nx * minD;
    pos.Z = t.Pos.Z + nz * minD;
    return true;
}

// Drive a tank with step-aware obstacle blocking; smashes destructible cover it rams.
void MoveTank(Tank t, float dx, float dz)
{
    MoveTankAxis(t, dx, 0);
    MoveTankAxis(t, 0, dz);
}

void MoveTankAxis(Tank t, float dx, float dz)
{
    if (MathF.Abs(dx) + MathF.Abs(dz) < 1e-6f) return;
    float nx = Math.Clamp(t.Pos.X + dx, -Arena + 3, Arena - 3);
    float nz = Math.Clamp(t.Pos.Z + dz, -Arena + 3, Arena - 3);
    bool blocked = false;
    for (int i = obstacles.Count - 1; i >= 0; i--)
    {
        var b = obstacles[i].Box;
        if (b.Max.Y < 0.35f) continue;
        if (nx > b.Min.X - TankCollideR && nx < b.Max.X + TankCollideR && nz > b.Min.Z - TankCollideR && nz < b.Max.Z + TankCollideR)
        {
            if (obstacles[i].Destructible) { obstacles.RemoveAt(i); t.SmashCd = 0.18f; }
            else blocked = true;
        }
    }
    if (!blocked) { t.Pos.X = nx; t.Pos.Z = nz; }
}

bool PointInObstacle(Vector3 p)
{
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z) return true;
    }
    return false;
}

Vector3 AimDir() => Vector3.Normalize(new(MathF.Cos(player.Pitch) * MathF.Sin(player.Yaw), MathF.Sin(player.Pitch), MathF.Cos(player.Pitch) * MathF.Cos(player.Yaw)));
Vector3 MuzzlePoint(Vector3 pos, float yaw, float pitch)
{
    Vector3 forward = Vector3.Normalize(new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));
    Vector3 flat = Vector3.Normalize(new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)));
    Vector3 right = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
    return pos + new Vector3(0, 0.05f, 0) + right * 0.28f + forward * 0.55f;
}
BoundingBox PlayerBounds(Player p) => PlayerBoundsForPos(p.Pos);
BoundingBox RemotePlayerBounds(RemotePlayer p) => PlayerBoundsForPos(p.Pos);
BoundingBox PlayerBoundsForPos(Vector3 pos) => new(pos + new Vector3(-0.62f, -1.7f, -0.62f), pos + new Vector3(0.62f, 0.65f, 0.62f));
BoundingBox TankBounds(Tank t) => new(t.Pos + new Vector3(-1.8f, 0, -2.4f), t.Pos + new Vector3(1.8f, 2.4f, 2.4f));
Vector3 PlayerCenter(Vector3 pos)
{
    var b = PlayerBoundsForPos(pos);
    return (b.Min + b.Max) * 0.5f;
}
Vector3 ApplySpread(Vector3 d, float s) => s <= 0 ? d : Vector3.Normalize(d + new Vector3((Rnd() - .5f) * s, (Rnd() - .5f) * s, (Rnd() - .5f) * s));
float Rnd() => (float)rng.NextDouble();

Vector2 RandSpot(float safe, float clearance)
{
    for (int i = 0; i < 100; i++)
    {
        float x = (Rnd() * 2 - 1) * (Arena - 5);
        float z = (Rnd() * 2 - 1) * (Arena - 5);
        if ((Math.Abs(x) > safe || Math.Abs(z) > safe) && !InsideObstacle(x, z, clearance)) return new Vector2(x, z);
    }
    return new Vector2(10 + Rnd() * 20, 10 + Rnd() * 20);
}

// A random, obstacle-free standing position (used for 2-player respawns).
Vector3 RandomSpawn()
{
    var p = RandSpot(0, PlayerRadius + 1f);
    return new Vector3(p.X, EyeHeight, p.Y);
}

float YawToCenter(Vector3 pos) => MathF.Atan2(-pos.X, -pos.Z);

bool InsideObstacle(float x, float z, float r)
{
    foreach (var o in obstacles)
    {
        var b = o.Box;
        if (x > b.Min.X - r && x < b.Max.X + r && z > b.Min.Z - r && z < b.Max.Z + r) return true;
    }
    return false;
}

void BroadcastHost()
{
    if (discoveryUdp == null || (DateTime.UtcNow - lastBroadcast).TotalSeconds < 1) return;
    lastBroadcast = DateTime.UtcNow;
    byte[] data = Encoding.UTF8.GetBytes($"SGDISC|{GamePort}|{playerName}");
    discoveryUdp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
}

void PumpDiscovery()
{
    if (discoveryUdp == null) return;
    while (discoveryUdp.Available > 0)
    {
        IPEndPoint ep = new(IPAddress.Any, 0);
        string msg = Encoding.UTF8.GetString(discoveryUdp.Receive(ref ep));
        string[] parts = msg.Split('|');
        if (parts.Length < 3 || parts[0] != "SGDISC") continue;
        int port = int.TryParse(parts[1], out int p) ? p : GamePort;
        var host = foundHosts.FirstOrDefault(h => h.Address.Equals(ep.Address));
        if (host == null) foundHosts.Add(new HostInfo(ep.Address, port, parts[2], DateTime.UtcNow));
        else { host.Name = parts[2]; host.LastSeen = DateTime.UtcNow; }
    }
    foundHosts.RemoveAll(h => (DateTime.UtcNow - h.LastSeen).TotalSeconds > 5);
    selectedHost = Math.Clamp(selectedHost, 0, Math.Max(0, foundHosts.Count - 1));
}

void JoinSelectedHost()
{
    var h = foundHosts[selectedHost];
    peerEndPoint = new IPEndPoint(h.Address, h.Port);
    SendNet($"JOIN|{playerName}");
    statusText = $"已送出加入要求到 {h.Address}，收到回應後按 Space 開始";
}

void PumpGameNetwork()
{
    if (gameUdp == null) return;
    while (gameUdp.Available > 0)
    {
        IPEndPoint ep = new(IPAddress.Any, 0);
        string msg = Encoding.UTF8.GetString(gameUdp.Receive(ref ep));
        string[] p = msg.Split('|');
        if (p.Length == 0) continue;
        if (p[0] == "JOIN")
        {
            peerEndPoint = ep;
            remote.Name = p.Length > 1 ? CleanName(p[1]) : "對手";
            SendNet($"WELCOME|{playerName}");
            statusText = $"找到對手 {remote.Name}，按 Enter 開始";
        }
        else if (p[0] == "WELCOME")
        {
            peerEndPoint = ep;
            remote.Name = p.Length > 1 ? CleanName(p[1]) : "對手";
            statusText = $"已連線 {remote.Name}，按 Space 開始";
        }
        else if (p[0] == "START")
        {
            remote.Name = p.Length > 1 ? CleanName(p[1]) : remote.Name;
            StartLanGame(mode == GameMode.LanHost);
        }
        else if (p[0] == "STATE" && p.Length >= 11)
        {
            remote.Name = CleanName(p[1]);
            remote.Pos = new Vector3(ParseF(p[2]), ParseF(p[3]), ParseF(p[4]));
            remote.Yaw = ParseF(p[5]);
            remote.Pitch = ParseF(p[6]);
            remote.Hp = ParseF(p[7]);
            remote.Weapon = int.TryParse(p[8], out int wi) ? wi : 0;
            remoteKills = int.TryParse(p[9], out int rk) ? rk : remoteKills;
            remote.Alive = p[10] == "1";
            if (p.Length >= 17)
            {
                player2Tank.Pos = new Vector3(ParseF(p[11]), 0, ParseF(p[12]));
                player2Tank.Yaw = ParseF(p[13]);
                player2Tank.Hp = ParseF(p[14]);
                player2Tank.Alive = p[15] == "1";
                remote.InTank = p[16] == "1";
            }
            lastPeerSeen = DateTime.UtcNow;
        }
        else if (p[0] == "HIT" && p.Length >= 5)
        {
            float dmg = ParseF(p[1]);
            Vector3 src = new(ParseF(p[2]), ParseF(p[3]), ParseF(p[4]));
            if (player.InTank && playerTank.Alive)
            {
                playerTank.Hp -= dmg;
                ShowDamageFrom(dmgIndP1, src, playerTank.Pos);
                if (playerTank.Hp <= 0) EjectAndDestroyTank(playerTank, player);
            }
            else if (respawnTimer <= 0 && player.Hp > 0)
            {
                player.Hp = Math.Max(0, player.Hp - dmg);
                ShowDamageFrom(dmgIndP1, src, player.Pos);
                if (player.Hp <= 0)
                {
                    respawnTimer = 3;
                    SendNet("KILL");
                }
            }
        }
        else if (p[0] == "THIT" && p.Length >= 2)
        {
            if (playerTank.Alive)
            {
                playerTank.Hp -= ParseF(p[1]);
                if (playerTank.Hp <= 0) EjectAndDestroyTank(playerTank, player);
            }
        }
        else if (p[0] == "KILL") localKills++;
    }
}

void SendNet(string msg)
{
    if (gameUdp == null || peerEndPoint == null) return;
    byte[] data = Encoding.UTF8.GetBytes(msg);
    gameUdp.Send(data, data.Length, peerEndPoint);
}

void SendState()
{
    if (peerEndPoint == null || (DateTime.UtcNow - lastStateSent).TotalMilliseconds < 50) return;
    lastStateSent = DateTime.UtcNow;
    SendNet(FormattableString.Invariant(
        $"STATE|{playerName}|{player.Pos.X:F2}|{player.Pos.Y:F2}|{player.Pos.Z:F2}|{player.Yaw:F3}|{player.Pitch:F3}|{player.Hp:F1}|{player.Weapon}|{localKills}|{(respawnTimer <= 0 && player.Hp > 0 ? 1 : 0)}|{playerTank.Pos.X:F2}|{playerTank.Pos.Z:F2}|{playerTank.Yaw:F3}|{playerTank.Hp:F1}|{(playerTank.Alive ? 1 : 0)}|{(player.InTank ? 1 : 0)}"));
}

void SendHit(float damage, Vector3 source)
{
    SendNet(FormattableString.Invariant($"HIT|{damage:F1}|{source.X:F2}|{source.Y:F2}|{source.Z:F2}"));
}

void SendTankHit(float damage)
{
    SendNet(FormattableString.Invariant($"THIT|{damage:F1}"));
}

float ParseF(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0;

void CloseNetwork()
{
    discoveryUdp?.Close();
    gameUdp?.Close();
    discoveryUdp = null;
    gameUdp = null;
    peerEndPoint = null;
}

IntPtr GetWindowHandleSafe()
{
    unsafe
    {
        void* h = Raylib.GetWindowHandle();
        return (IntPtr)h;
    }
}

void ReadNameInput() => playerName = EditName(playerName);

string EditName(string name)
{
    while (true)
    {
        int cp = Raylib.GetCharPressed();
        if (cp <= 0) break;
        if (cp >= 32 && cp != 127 && name.Length < 18) name += char.ConvertFromUtf32(cp);
    }
    if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && name.Length > 0)
    {
        int remove = char.IsLowSurrogate(name[^1]) && name.Length > 1 ? 2 : 1;
        name = name[..^remove];
    }
    if ((Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)) && Raylib.IsKeyPressed(KeyboardKey.V))
    {
        string clip = Raylib.GetClipboardText_();
        if (!string.IsNullOrWhiteSpace(clip)) name = CleanName(clip);
    }
    return name;
}

string CleanName(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return "玩家";
    string cleaned = s.Trim().Replace("|", "").Replace("\n", "").Replace("\r", "");
    return cleaned.Length == 0 ? "玩家" : cleaned[..Math.Min(18, cleaned.Length)];
}

void LoadRankings()
{
    rankings.Clear();
    try
    {
        if (!File.Exists(scoreFile)) return;
        foreach (var line in File.ReadAllLines(scoreFile))
        {
            var p = line.Split('|', 4);
            if (p.Length == 4 && int.TryParse(p[0], out int sc) && int.TryParse(p[1], out int wv))
                rankings.Add(new ScoreEntry(sc, wv, p[2] == "1", p[3]));
        }
    }
    catch { }
}

void RecordScore(string name, int sc, int wv, bool victory)
{
    rankings.Add(new ScoreEntry(sc, wv, victory, CleanName(name)));
    rankings.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : b.Wave.CompareTo(a.Wave));
    if (rankings.Count > 10) rankings.RemoveRange(10, rankings.Count - 10);
    try { File.WriteAllLines(scoreFile, rankings.Select(r => $"{r.Score}|{r.Wave}|{(r.Victory ? 1 : 0)}|{r.Name}")); } catch { }
}

void ShowBanner(string text, float seconds) { bannerText = text; bannerTimer = seconds; }

// Localisation: pick the Traditional-Chinese or English string for the current language.
string T(string zh, string en) => english ? en : zh;
string WeaponName(int i) => english ? WeaponEn[i] : weapons[i].Name;

void Draw()
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(25, 32, 44, 255));

    if (state == AppState.Start) DrawStart();
    else if (state == AppState.SplitNames) DrawSplitNames();
    else if (state == AppState.HostLobby) DrawHostLobby();
    else if (state == AppState.JoinLobby) DrawJoinLobby();
    else if (state == AppState.LocalMouseSelect) DrawLocalMouseSelect();
    else
    {
        if (mode == GameMode.LocalSplit)
        {
            DrawSplitWorld();
            DrawSplitHud();
        }
        else
        {
            DrawWorld3D();
            DrawHud();
        }
        if (state == AppState.Paused) DrawCenterPanel(T("暫停", "Paused"), T("P / ESC 繼續  F2 重新開始  M 主選單  Ctrl+Q 離開", "P / ESC resume  F2 restart  M menu  Ctrl+Q quit"));
        if (state == AppState.GameOver)
        {
            string title;
            string endText;
            string back = T("，按 Enter 返回", " — Enter to return");
            if (mode == GameMode.Single) { title = T(won ? "勝利" : "失敗", won ? "Victory" : "Defeat"); endText = T($"分數 {score}", $"Score {score}") + back; }
            else if (mode == GameMode.LocalSplit)
            {
                title = p1Kills == p2Kills ? T("平手", "Draw") : $"{(p1Kills > p2Kills ? playerName : player2Name)} " + T("勝利", "wins");
                endText = $"{playerName} {p1Kills} : {p2Kills} {player2Name}" + back;
            }
            else { title = T(won ? "勝利" : "失敗", won ? "Victory" : "Defeat"); endText = $"{playerName} {localKills} : {remoteKills} {remote.Name}" + back; }
            DrawCenterPanel(title, endText);
        }
    }
    Raylib.EndDrawing();
}

void DrawStart()
{
    DrawTextC(T("刀劍與槍", "Sword & Gun"), 72, 70, 64, Color.RayWhite, true);
    DrawTextC(T("玩家名字", "Player name"), 0, 160, 24, Color.Yellow, true);
    Raylib.DrawRectangle(Raylib.GetScreenWidth() / 2 - 210, 194, 420, 44, new Color(0, 0, 0, 140));
    DrawTextC(playerName.Length == 0 ? T("請輸入名字，可貼上繁體中文", "Type a name (Ctrl+V to paste)") : playerName, 0, 203, 24, playerName.Length == 0 ? Color.Gray : Color.White, true);
    string[] items = english
        ? new[] { "Single-player waves", "Same-PC split-screen (2 keyboards + 2 mice)", "Host LAN room (broadcast IP)", "Find & join LAN room" }
        : new[] { "單人波次", "同機雙人分割對戰（兩套鍵盤 + 兩支滑鼠）", "建立區網房間（廣播 IP）", "搜尋並加入區網房間" };
    for (int i = 0; i < items.Length; i++) DrawTextC((menuIndex == i ? "> " : "   ") + items[i], 0, 290 + i * 48, 28, menuIndex == i ? Color.Yellow : Color.White, true);
    // Single-player high-score ranking.
    DrawTextC(T("排行榜", "Ranking"), 0, 500, 26, Color.Gold, true);
    if (rankings.Count == 0)
        DrawTextC(T("尚無排名", "No scores yet"), 0, 540, 20, Color.Gray, true);
    else
        for (int i = 0; i < rankings.Count && i < 8; i++)
        {
            var r = rankings[i];
            string tag = r.Victory ? T("勝利", "Victory") : T($"第{r.Wave}波", $"Wave {r.Wave}");
            DrawTextC($"#{i + 1}  {r.Name}   {r.Score}   {tag}", 0, 538 + i * 30, 20, i == 0 ? Color.Yellow : Color.White, true);
        }
    DrawTextC(rawInputAvailable ? T("Raw Input 已啟用，可辨識多支滑鼠", "Raw Input ready — multiple mice supported") : T("Raw Input 不可用，同機雙滑鼠可能無法使用", "Raw Input unavailable — two mice may not work"), 0, Raylib.GetScreenHeight() - 90, 20, rawInputAvailable ? Color.Lime : Color.Orange, true);
    DrawTextC(T("Enter 開始 · 上下鍵選擇 · Ctrl+V 貼上名字 · F10 English · ESC 離開", "Enter start · Up/Down select · Ctrl+V paste · F10 中文 · ESC quit"), 0, Raylib.GetScreenHeight() - 60, 20, Color.SkyBlue, true);
}

void DrawSplitNames()
{
    DrawTextC(T("雙人對戰 — 設定", "Versus — setup"), 0, 70, 46, Color.RayWhite, true);
    DrawNameField(T("玩家 1 (P1)", "Player 1 (P1)"), playerName, T("玩家1", "Player1"), 160, splitNameActive == 0, PlayerColorP1);
    DrawNameField(T("玩家 2 (P2)", "Player 2 (P2)"), player2Name, T("玩家2", "Player2"), 270, splitNameActive == 1, PlayerColorP2);
    string wc = winCond == WinCondition.Score
        ? T($"先到 {winScore} 殺獲勝", $"First to {winScore} kills")
        : T($"限時 {timeLimitSec / 60:0.#} 分鐘（殺敵多者勝）", $"{timeLimitSec / 60:0.#} min — most kills wins");
    DrawTextC(T("勝利條件", "Win condition"), 0, 390, 24, splitNameActive == 2 ? Color.Yellow : Color.White, true);
    DrawTextC(wc, 0, 426, 28, splitNameActive == 2 ? Color.Yellow : Color.SkyBlue, true);
    DrawTextC(T("Tab 切換欄位 · 左右鍵 切換條件 · 上下鍵 調整 · Ctrl+V 貼上", "Tab field · Left/Right mode · Up/Down adjust · Ctrl+V paste"), 0, Raylib.GetScreenHeight() - 96, 20, Color.Gray, true);
    DrawTextC(T("Enter 開始 · ESC 返回", "Enter start · ESC back"), 0, Raylib.GetScreenHeight() - 66, 22, Color.SkyBlue, true);
}

void DrawNameField(string label, string value, string placeholder, int y, bool active, Color accent)
{
    int w = Raylib.GetScreenWidth();
    DrawTextC(label, 0, y, 24, active ? Color.Yellow : accent, true);
    Raylib.DrawRectangle(w / 2 - 220, y + 32, 440, 46, new Color(0, 0, 0, 150));
    if (active) Raylib.DrawRectangleLines(w / 2 - 220, y + 32, 440, 46, Color.Yellow);
    bool empty = value.Length == 0;
    DrawTextC(empty ? placeholder : value, 0, y + 42, 26, empty ? Color.Gray : Color.White, true);
}

void DrawHostLobby()
{
    DrawTextC(T("建立區網房間", "Host LAN room"), 0, 90, 52, Color.RayWhite, true);
    DrawTextC(statusText, 0, 180, 24, Color.Yellow, true);
    DrawTextC(T("同網段玩家會自動看到你的 IP。有人加入後按 Enter 開始。", "Players on your LAN see your IP automatically. Press Enter once someone joins."), 0, 230, 22, Color.White, true);
    DrawTextC(peerEndPoint == null ? T("尚未找到對手", "No opponent yet") : T($"對手：{remote.Name}  {peerEndPoint.Address}", $"Opponent: {remote.Name}  {peerEndPoint.Address}"), 0, 310, 28, peerEndPoint == null ? Color.Orange : Color.Lime, true);
    DrawTextC(T("ESC 返回", "ESC back"), 0, Raylib.GetScreenHeight() - 70, 22, Color.Gray, true);
}

void DrawJoinLobby()
{
    DrawTextC(T("搜尋區網房間", "Find LAN room"), 0, 70, 52, Color.RayWhite, true);
    DrawTextC(statusText, 0, 140, 22, Color.Yellow, true);
    if (foundHosts.Count == 0) DrawTextC(T("尚未找到房間，確認兩台電腦在同一個 LAN，且防火牆允許 UDP。", "No rooms found — ensure both PCs are on the same LAN and UDP is allowed."), 0, 230, 22, Color.Orange, true);
    for (int i = 0; i < foundHosts.Count; i++)
    {
        var h = foundHosts[i];
        DrawTextC($"{(i == selectedHost ? ">" : " ")} {h.Name}  {h.Address}:{h.Port}", 0, 220 + i * 36, 24, i == selectedHost ? Color.Yellow : Color.White, true);
    }
    DrawTextC(peerEndPoint == null ? T("Enter 加入選取房間", "Enter to join selected room") : T("已連線，按 Space 開始", "Connected — press Space to start"), 0, Raylib.GetScreenHeight() - 100, 22, Color.SkyBlue, true);
    DrawTextC(T("ESC 返回", "ESC back"), 0, Raylib.GetScreenHeight() - 70, 22, Color.Gray, true);
}

void DrawLocalMouseSelect()
{
    DrawTextC(T("同機雙人指向裝置設定", "Two-player pointing-device setup"), 0, 60, 46, Color.RayWhite, true);
    DrawTextC(localMouseStage == 0 ? T("請 P1 晃動或點一下自己的裝置", "P1: wiggle or click your own device") : T("請 P2 晃動或點一下另一支裝置", "P2: wiggle or click the other device"), 0, 134, 26, Color.Yellow, true);
    DrawTextC(T($"偵測到的指向裝置：{RawMouse.Devices.Count}（滑鼠各自獨立；觸控板需 Precision 驅動支援）", $"Pointing devices detected: {RawMouse.Devices.Count} (mice are separate; touchpads need Precision drivers)"), 0, 178, 20, Color.SkyBlue, true);
    int y = 216;
    foreach (var d in RawMouse.Devices.Values.OrderBy(v => v.Index))
    {
        string type = d.IsTouch ? T("觸控板", "touchpad") : T("滑鼠", "mouse");
        string who = (p1Mouse != IntPtr.Zero && RawMouse.SameGroup(d.Handle, p1Mouse)) ? "  → P1"
            : (p2Mouse != IntPtr.Zero && RawMouse.SameGroup(d.Handle, p2Mouse)) ? "  → P2" : "";
        Color col = who.Length > 0 ? Color.Lime : Color.White;
        DrawTextC(T($"#{d.Index} [{type}]  移動 {d.MoveTotal}  點擊 {d.Clicks}{who}", $"#{d.Index} [{type}]  move {d.MoveTotal}  clicks {d.Clicks}{who}"), 0, y, 20, col, true);
        y += 26;
    }
    if (RawMouse.Devices.Count == 0) DrawTextC(T("（移動滑鼠或觸控板即可顯示裝置）", "(move a mouse or touchpad to list devices)"), 0, y, 20, Color.Gray, true);
    DrawTextC(T("先各自動一動確認是兩個不同裝置，再依序晃動（或點擊）指定 P1、P2", "Wiggle each to confirm two separate devices, then wiggle (or click) to assign P1 then P2"), 0, Raylib.GetScreenHeight() - 116, 18, Color.Gray, true);
    DrawTextC(T("P1：WASD + 裝置一    P2：方向鍵 + 裝置二", "P1: WASD + device 1    P2: Arrows + device 2"), 0, Raylib.GetScreenHeight() - 88, 20, Color.White, true);
    DrawTextC(T("ESC 返回", "ESC back"), 0, Raylib.GetScreenHeight() - 56, 20, Color.Gray, true);
}

// Camera eye for a player: normal first-person, or raised/behind when driving a tank
// so the turret and barrel don't block the view.
Vector3 CameraEye(Player p, Tank t)
{
    if (p.InTank && t.Alive)
    {
        Vector3 flat = new(MathF.Sin(p.Yaw), 0, MathF.Cos(p.Yaw));
        return t.Pos + new Vector3(0, 3.6f, 0) - flat * 2.6f;
    }
    return p.Pos + new Vector3(0, 0.2f, 0);
}

void DrawWorld3D()
{
    Vector3 eye = CameraEye(player, playerTank);
    var cam = new Camera3D(eye, eye + AimDir(), Vector3.UnitY, player.Fov, CameraProjection.Perspective);
    Raylib.BeginMode3D(cam);
    Raylib.BeginShaderMode(lightShader);
    Raylib.DrawPlane(Vector3.Zero, new Vector2(Arena * 2, Arena * 2), new Color(58, 80, 55, 255));
    foreach (var o in obstacles) DrawBox(o.Box, o.Destructible ? new Color(130, 120, 92, 255) : new Color(90, 80, 65, 255));
    foreach (var p in pickups) if (p.Active) { Raylib.DrawCube(p.Pos + new Vector3(0, .4f, 0), 1, .3f, 1, Color.White); Raylib.DrawCube(p.Pos + new Vector3(0, .7f, 0), .25f, .7f, .25f, Color.Green); }
    foreach (var c in weaponCrates) if (c.Active) Raylib.DrawCube(c.Pos + new Vector3(0, .45f, 0), 1.1f, .9f, 1.1f, Color.Gold);
    foreach (var e in enemies) if (e.Alive) DrawEnemy(e);
    if (mode == GameMode.Single && ally.Alive)
        DrawAvatar(ally.Pos + new Vector3(0, EyeHeight, 0), ally.Yaw, 0, 0, 0, 0, new Color(50, 140, 225, 255), new Color(140, 200, 255, 255));
    DrawTank(playerTank, FriendlyTankBody, FriendlyTankTurret);
    if (mode != GameMode.Single) DrawTank(player2Tank, EnemyTankBody, EnemyTankTurret);
    if (mode != GameMode.Single && remote.Alive && !remote.InTank && (DateTime.UtcNow - lastPeerSeen).TotalSeconds < 3) DrawRemote();
    DrawViewModel(player);
    foreach (var b in bullets) Raylib.DrawSphere(b.Pos, .12f, Color.Yellow);
    foreach (var r in rockets) Raylib.DrawSphere(r.Pos, .22f, Color.Orange);
    Raylib.EndShaderMode();
    Raylib.DrawGrid(156, 2);
    foreach (var t in tracers) Raylib.DrawLine3D(t.From, t.To, t.Color);
    DrawFlashes();
    Raylib.EndMode3D();
    if (mode == GameMode.Single) DrawEnemyHpBars(cam, eye, AimDir());
    if (mode == GameMode.Single && ally.Alive) DrawWorldHpBar(ally.Pos + new Vector3(0, 2.45f, 0), ally.Hp / ally.MaxHp, cam, eye, AimDir(), Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), 46);
    int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
    if (playerTank.Alive) DrawWorldHpBar(playerTank.Pos + new Vector3(0, 3.2f, 0), playerTank.Hp / playerTank.MaxHp, cam, eye, AimDir(), sw, sh, 90);
    if (mode != GameMode.Single && player2Tank.Alive) DrawWorldHpBar(player2Tank.Pos + new Vector3(0, 3.2f, 0), player2Tank.Hp / player2Tank.MaxHp, cam, eye, AimDir(), sw, sh, 90);
    if (mode != GameMode.Single && remote.Alive && !remote.InTank && (DateTime.UtcNow - lastPeerSeen).TotalSeconds < 3)
        DrawNameLabel(remote.Name, remote.Pos + new Vector3(0, 1.15f, 0), cam, eye, AimDir(), Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
}

void DrawSplitWorld()
{
    int w = Raylib.GetScreenWidth();
    int h = Raylib.GetScreenHeight();
    int halfW = w / 2;
    EnsureFontForText(playerName); EnsureFontForText(player2Name);  // bake names before render-to-texture
    EnsureSplitTextures(halfW, h);
    DrawWorldFor(splitTexL, player, player2, playerTank, player2Tank);
    DrawWorldFor(splitTexR, player2, player, player2Tank, playerTank);
    // Render textures are stored upside down, so source height is negated.
    var src = new Rectangle(0, 0, halfW, -h);
    Raylib.DrawTextureRec(splitTexL.Texture, src, new Vector2(0, 0), Color.White);
    Raylib.DrawTextureRec(splitTexR.Texture, src, new Vector2(halfW, 0), Color.White);
    Raylib.DrawRectangle(halfW - 2, 0, 4, h, Color.Black);
}

void EnsureSplitTextures(int halfW, int h)
{
    if (splitTexW == halfW && splitTexH == h) return;
    if (splitTexW != 0)
    {
        Raylib.UnloadRenderTexture(splitTexL);
        Raylib.UnloadRenderTexture(splitTexR);
    }
    splitTexL = Raylib.LoadRenderTexture(halfW, h);
    splitTexR = Raylib.LoadRenderTexture(halfW, h);
    Raylib.SetTextureFilter(splitTexL.Texture, TextureFilter.Bilinear);
    Raylib.SetTextureFilter(splitTexR.Texture, TextureFilter.Bilinear);
    splitTexW = halfW;
    splitTexH = h;
}

// Renders one player's view into its own render texture. Using a half-width
// texture (rather than scissor-clipping a full-window render) makes the camera
// projection use the half-viewport aspect ratio, so the camera forward axis maps
// to the centre of the half — i.e. exactly under the crosshair. Without this the
// shot direction and the crosshair disagree and players can never hit each other.
void DrawWorldFor(RenderTexture2D target, Player view, Player opponent, Tank ownTank, Tank oppTank)
{
    Raylib.BeginTextureMode(target);
    Raylib.ClearBackground(new Color(25, 32, 44, 255));
    Vector3 dir = Vector3.Normalize(new(MathF.Cos(view.Pitch) * MathF.Sin(view.Yaw), MathF.Sin(view.Pitch), MathF.Cos(view.Pitch) * MathF.Cos(view.Yaw)));
    Vector3 eye = CameraEye(view, ownTank);
    var cam = new Camera3D(eye, eye + dir, Vector3.UnitY, view.Fov, CameraProjection.Perspective);
    Raylib.BeginMode3D(cam);
    Raylib.BeginShaderMode(lightShader);
    Raylib.DrawPlane(Vector3.Zero, new Vector2(Arena * 2, Arena * 2), new Color(58, 80, 55, 255));
    foreach (var o in obstacles) DrawBox(o.Box, o.Destructible ? new Color(130, 120, 92, 255) : new Color(90, 80, 65, 255));
    foreach (var p in pickups) if (p.Active) { Raylib.DrawCube(p.Pos + new Vector3(0, .4f, 0), 1, .3f, 1, Color.White); Raylib.DrawCube(p.Pos + new Vector3(0, .7f, 0), .25f, .7f, .25f, Color.Green); }
    foreach (var c in weaponCrates) if (c.Active) Raylib.DrawCube(c.Pos + new Vector3(0, .45f, 0), 1.1f, .9f, 1.1f, Color.Gold);
    if (opponent.Hp > 0 && !opponent.InTank)
    {
        bool oppIsP2 = opponent == player2;
        DrawAvatar(opponent.Pos, opponent.Yaw, opponent.Pitch, opponent.Weapon, opponent.SwordAnim, opponent.HitFlash,
            oppIsP2 ? PlayerColorP2 : PlayerColorP1, oppIsP2 ? PlayerColorP2Head : PlayerColorP1Head);
    }
    DrawTank(ownTank, FriendlyTankBody, FriendlyTankTurret);
    DrawTank(oppTank, EnemyTankBody, EnemyTankTurret);
    DrawViewModel(view);
    foreach (var r in rockets) Raylib.DrawSphere(r.Pos, r.FromTank ? .35f : .22f, Color.Orange);
    Raylib.EndShaderMode();
    Raylib.DrawGrid(156, 2);
    foreach (var t in tracers) Raylib.DrawLine3D(t.From, t.To, t.Color);
    DrawFlashes();
    Raylib.EndMode3D();
    int tw = target.Texture.Width, th = target.Texture.Height;
    if (ownTank.Alive) DrawWorldHpBar(ownTank.Pos + new Vector3(0, 3.2f, 0), ownTank.Hp / ownTank.MaxHp, cam, eye, dir, tw, th, 86);
    if (oppTank.Alive) DrawWorldHpBar(oppTank.Pos + new Vector3(0, 3.2f, 0), oppTank.Hp / oppTank.MaxHp, cam, eye, dir, tw, th, 86);
    if (opponent.Hp > 0)
    {
        string nm = opponent == player2 ? player2Name : playerName;
        Vector3 head = opponent.InTank && oppTank.Alive ? oppTank.Pos + new Vector3(0, 3.4f, 0) : opponent.Pos + new Vector3(0, 1.15f, 0);
        DrawNameLabel(nm, head, cam, eye, dir, tw, th);
    }
    Raylib.EndTextureMode();
}

// Draw a 2D name tag at the projected screen position of a world point (head).
void DrawNameLabel(string name, Vector3 head, Camera3D cam, Vector3 eye, Vector3 dir, int viewW, int viewH)
{
    if (string.IsNullOrEmpty(name)) return;
    if (Vector3.Dot(Vector3.Normalize(head - eye), dir) <= 0.1f) return;  // behind the camera
    Vector2 sp = Raylib.GetWorldToScreenEx(head, cam, viewW, viewH);
    const float size = 20;
    Vector2 m = Raylib.MeasureTextEx(uiFontSmall, name, size, 1);
    Vector2 pos = new(sp.X - m.X / 2f, sp.Y - m.Y);
    Raylib.DrawTextEx(uiFontSmall, name, pos + new Vector2(1.5f, 1.5f), size, 1, Color.Black);
    Raylib.DrawTextEx(uiFontSmall, name, pos, size, 1, Color.White);
}

void DrawTank(Tank t, Color body, Color turret)
{
    if (!t.Alive) return;
    Color tread = new(40, 40, 46, 255);
    Rlgl.PushMatrix();
    Rlgl.Translatef(t.Pos.X, t.Pos.Y, t.Pos.Z);
    Rlgl.Rotatef(t.Yaw * (180f / MathF.PI), 0, 1, 0);
    Raylib.DrawCube(new Vector3(0, 0.9f, 0), 3.6f, 1.4f, 4.4f, body);
    Raylib.DrawCube(new Vector3(0, 1.85f, 0), 2.1f, 1.0f, 2.2f, turret);
    Raylib.DrawCube(new Vector3(0, 1.95f, 2.0f), 0.45f, 0.45f, 3.3f, tread);
    Raylib.DrawCube(new Vector3(-1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, tread);
    Raylib.DrawCube(new Vector3(1.35f, 0.25f, 0), 0.55f, 0.55f, 4.8f, tread);
    Rlgl.PopMatrix();
}

void DrawBox(BoundingBox b, Color c)
{
    Vector3 size = b.Max - b.Min;
    Vector3 center = (b.Max + b.Min) * .5f;
    Raylib.DrawCube(center, size.X, size.Y, size.Z, c);
}

void DrawEnemy(Enemy e)
{
    // Brighten briefly when hit (lerp toward a vivid white-hot tint) rather than going
    // flat white, which the directional shading would render as grey on shadowed faces.
    bool hit = e.HitFlash > 0;
    Color Flash(Color c) => hit ? new Color((byte)Math.Min(255, c.R + 90), (byte)Math.Min(255, c.G + 90), (byte)Math.Min(255, c.B + 90), (byte)255) : c;
    if (e.Type == EnemyType.Tank)
    {
        Raylib.DrawCube(e.Pos + new Vector3(0, 0.9f, 0), 3.5f, 1.4f, 4.2f, Flash(new Color(220, 60, 70, 255)));            // hull
        Raylib.DrawCube(e.Pos + new Vector3(0, 1.85f, 0), 2.0f, 1.0f, 2.0f, Flash(new Color(245, 90, 95, 255)));          // turret
        Raylib.DrawCube(e.Pos + new Vector3(0, 1.95f, 2.0f), 0.4f, 0.4f, 3.0f, new Color(40, 40, 46, 255));               // barrel
        return;
    }
    // Vivid, saturated colours so enemies pop against the world.
    Color body = Flash(e.Type == EnemyType.Shooter ? new Color(235, 45, 55, 255) : new Color(255, 140, 0, 255));
    Color headCol = Flash(e.Type == EnemyType.Shooter ? new Color(255, 130, 135, 255) : new Color(255, 200, 110, 255));
    Color legCol = Flash(e.Type == EnemyType.Shooter ? new Color(170, 30, 40, 255) : new Color(190, 100, 0, 255));
    Raylib.DrawCube(e.Pos + new Vector3(0, 0.45f, 0), 0.66f, 0.9f, 0.45f, legCol);  // legs
    Raylib.DrawCube(e.Pos + new Vector3(0, 1.25f, 0), 0.95f, 0.85f, 0.55f, body);   // torso
    Raylib.DrawCube(e.Pos + new Vector3(0, 1.95f, 0), 0.6f, 0.6f, 0.6f, headCol);   // head
    // A gun pointed at the player so shooters/brawlers read as armed.
    Vector3 to = player.Pos - e.Pos; to.Y = 0;
    float yaw = MathF.Atan2(to.X, to.Z);
    DrawHeldWeapon(e.Pos + new Vector3(0, 1.3f, 0) + RightOf(yaw) * 0.34f, yaw, 0, e.Type == EnemyType.Brawler ? 4 : 0, 0);
}

// 2D HP bars over each living enemy (single-player). Projected from the world each frame.
// A health bar projected to screen above a world point (for tanks; viewW/viewH let it
// work both on the full screen and inside a split render-texture).
void DrawWorldHpBar(Vector3 worldTop, float frac, Camera3D cam, Vector3 eye, Vector3 dir, int viewW, int viewH, float barW)
{
    if (Vector3.Dot(Vector3.Normalize(worldTop - eye), dir) <= 0.1f) return;   // behind camera
    Vector2 sp = Raylib.GetWorldToScreenEx(worldTop, cam, viewW, viewH);
    frac = Math.Clamp(frac, 0f, 1f);
    const float bh = 7;
    float x = sp.X - barW / 2, y = sp.Y;
    Raylib.DrawRectangle((int)x - 1, (int)y - 1, (int)barW + 2, (int)bh + 2, new Color(0, 0, 0, 205));
    Raylib.DrawRectangle((int)x, (int)y, (int)(barW * frac), (int)bh, new Color((byte)(220 * (1 - frac) + 30), (byte)(70 + 165 * frac), (byte)55, (byte)255));
}

void DrawEnemyHpBars(Camera3D cam, Vector3 eye, Vector3 dir)
{
    int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
    foreach (var e in enemies)
    {
        if (!e.Alive) continue;
        Vector3 head = e.Pos + new Vector3(0, e.Type == EnemyType.Tank ? 3.1f : 2.45f, 0);
        if (Vector3.Dot(Vector3.Normalize(head - eye), dir) <= 0.1f) continue;   // behind camera
        Vector2 sp = Raylib.GetWorldToScreen(head, cam);
        float frac = Math.Clamp(e.Hp / Math.Max(1f, e.MaxHp), 0f, 1f);
        float bw = e.Type == EnemyType.Tank ? 90 : 46, bh = 6;
        float x = sp.X - bw / 2, y = sp.Y;
        Raylib.DrawRectangle((int)x - 1, (int)y - 1, (int)bw + 2, (int)bh + 2, new Color(0, 0, 0, 200));
        Raylib.DrawRectangle((int)x, (int)y, (int)(bw * frac), (int)bh, new Color((byte)(235 * (1 - frac) + 20), (byte)(60 + 175 * frac), (byte)50, (byte)255));
    }
}

void DrawRemote()
{
    // In LAN the local player is invisible (first-person); colour the opponent by role.
    bool remoteIsP2 = mode == GameMode.LanHost;
    DrawAvatar(remote.Pos, remote.Yaw, remote.Pitch, remote.Weapon, 0,
        0, remoteIsP2 ? PlayerColorP2 : PlayerColorP1, remoteIsP2 ? PlayerColorP2Head : PlayerColorP1Head);
}

void DrawAvatar(Vector3 pos, float yaw, float pitch, int weapon, float swordAnim, float hitFlash, Color body, Color head)
{
    bool hit = hitFlash > 0;
    Color bodyC = hit ? Color.White : body;
    Color headC = hit ? Color.White : head;
    Color legC = hit ? Color.White : new Color((byte)(body.R * 0.7f), (byte)(body.G * 0.7f), (byte)(body.B * 0.7f), (byte)255);
    float feet = pos.Y - EyeHeight;
    Vector3 bp = new(pos.X, feet, pos.Z);
    Raylib.DrawCube(bp + new Vector3(0, 0.45f, 0), 0.66f, 0.9f, 0.45f, legC);     // legs
    Raylib.DrawCube(bp + new Vector3(0, 1.25f, 0), 0.92f, 0.82f, 0.55f, bodyC);   // torso
    Raylib.DrawCube(bp + new Vector3(0, 1.95f, 0), 0.58f, 0.58f, 0.58f, headC);   // head
    Vector3 hand = bp + new Vector3(0, 1.3f, 0) + RightOf(yaw) * 0.34f + FlatFwd(yaw) * 0.12f;
    DrawHeldWeapon(hand, yaw, pitch, weapon, swordAnim);
}

// A blocky held weapon (or sword, with a swing arc) oriented along the aim direction.
void DrawHeldWeapon(Vector3 origin, float yaw, float pitch, int weapon, float swordAnim)
{
    var w = weapons[weapon];
    Color metal = new(45, 45, 52, 255);
    Rlgl.PushMatrix();
    Rlgl.Translatef(origin.X, origin.Y, origin.Z);
    Rlgl.Rotatef(yaw * Rad2Deg, 0, 1, 0);
    Rlgl.Rotatef(-pitch * Rad2Deg, 1, 0, 0);
    if (w.Melee)
    {
        float t = swordAnim > 0 ? MathF.Sin(Math.Clamp((0.3f - swordAnim) / 0.3f, 0, 1) * MathF.PI) : 0f;  // 0→1→0
        if (weapon == FistsIndex)
        {
            Rlgl.Translatef(0, 0, t * 0.3f);   // punch thrust
            Raylib.DrawCube(new Vector3(0, 0, 0.22f), 0.22f, 0.2f, 0.26f, new Color(225, 180, 150, 255));  // fist
        }
        else
        {
            Rlgl.Rotatef(70f - t * 120f, 1, 0, 0);   // raised → swung-down arc
            Raylib.DrawCube(new Vector3(0, 0, 0.22f), 0.05f, 0.05f, 0.44f, new Color(38, 40, 48, 255));    // 柄 wrapped handle
            Raylib.DrawCube(new Vector3(0, 0, 0.45f), 0.18f, 0.18f, 0.04f, new Color(78, 64, 40, 255));    // 鍔 tsuba guard
            Raylib.DrawCube(new Vector3(0, 0, 1.32f), 0.035f, 0.10f, 1.6f, new Color(214, 220, 230, 255)); // long slim katana blade
        }
    }
    else
    {
        float len = w.Name == "狙擊槍" ? 1.15f : w.Explosive ? 1.0f : 0.66f;
        Raylib.DrawCube(new Vector3(0, 0.02f, len * 0.5f), 0.13f, 0.17f, len, metal);   // receiver / barrel
        Raylib.DrawCube(new Vector3(0, -0.17f, 0.06f), 0.1f, 0.26f, 0.13f, metal);      // grip
        if (w.Name == "狙擊槍") Raylib.DrawCube(new Vector3(0, 0.16f, 0.35f), 0.06f, 0.06f, 0.34f, metal);  // scope
    }
    Rlgl.PopMatrix();
}

// First-person weapon for the local view; hidden while aiming (down-sights) or in a tank.
void DrawViewModel(Player p)
{
    if (p.InTank || p.Fov < 70f) return;
    Vector3 fwd = AimVec(p.Yaw, p.Pitch);
    Vector3 eye = p.Pos + new Vector3(0, 0.2f, 0);
    float cd = weapons[p.Weapon].Cooldown;
    float recoil = cd > 0 && p.Cooldown > 0 ? Math.Clamp(p.Cooldown / cd, 0, 1) : 0;
    Vector3 origin = eye + fwd * (0.55f - recoil * 0.12f) + RightOf(p.Yaw) * 0.17f + new Vector3(0, -0.16f, 0);
    DrawHeldWeapon(origin, p.Yaw, p.Pitch, p.Weapon, p.SwordAnim);
}

void DrawFlashes()
{
    foreach (var f in flashes)
    {
        float r = 0.4f * Math.Clamp(f.Life / 0.05f, 0f, 1f);
        Raylib.DrawSphere(f.Pos, r, new Color(255, 236, 150, 255));
    }
}

void DrawHud()
{
    int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
    if (player.Weapon == SniperIndex && !player.InTank && player.Fov < 30f) DrawScope(w / 2f, h / 2f, Math.Min(w, h) * 0.42f);
    DrawCrosshair(w / 2, h / 2);
    DrawDamageArrows(new Vector2(w / 2f, h / 2f), player.Yaw, dmgIndP1);
    DrawTextC(mode == GameMode.Single ? T($"第 {wave}/8 波  分數 {score}  敵人 {enemies.Count}", $"Wave {wave}/8  Score {score}  Enemies {enemies.Count}") : T($"區網對戰  {playerName} {localKills} : {remoteKills} {remote.Name}", $"LAN duel  {playerName} {localKills} : {remoteKills} {remote.Name}"), 20, 20, 24, Color.White, false);
    DrawTextC(T("WASD 移動 · 空白 跳 · 1-6/Q 切換武器 · R 換彈 · F 坦克 · F2 重來 · M 選單 · Ctrl+Q 離開", "WASD move · Space jump · 1-6/Q weapon · R reload · F tank · F2 restart · M menu · Ctrl+Q quit"), 20, 50, 18, new Color(205, 205, 205, 190), false);
    DrawHpBar(20, h - 56, 250, 30, player.Hp, PlayerMaxHp);
    var wep = weapons[player.Weapon];
    string ammo = wep.MagSize == 0 ? "" : $" {player.Ammo[player.Weapon]}/{wep.MagSize}";
    DrawTextC($"{player.Weapon + 1}. {WeaponName(player.Weapon)}{ammo}", w - 300, h - 52, 24, Color.White, false);
    DrawWeaponBar(player, P1WeaponLabels, w / 2f, h - 30, 20);
    DrawTankStatus(player, playerTank, 20, h - 84, "F");
    if (respawnTimer > 0) DrawTextC(T($"重生 {respawnTimer:F1}s", $"Respawn {respawnTimer:F1}s"), 0, h / 2 + 60, 34, Color.Orange, true);
    // Wave announcement: large centred banner with a dark backing strip.
    if (bannerTimer > 0)
    {
        float a = Math.Clamp(bannerTimer, 0f, 1f);
        Raylib.DrawRectangle(0, (int)(h * 0.22f) - 8, w, 84, Raylib.Fade(Color.Black, 0.45f * a));
        DrawTextC(bannerText, 0, h * 0.22f, 60, Raylib.Fade(Color.Yellow, a), true);
    }
    // Countdown to the next wave during the breather.
    if (mode == GameMode.Single && waveDelay > 0 && enemies.Count == 0)
        DrawTextC(T($"下一波 {MathF.Ceiling(waveDelay)}", $"Next wave {MathF.Ceiling(waveDelay)}"), 0, h * 0.22f + 78, 30, Color.SkyBlue, true);
}

// Horizontal weapon bar showing all six slots: owned bright, not-yet-found dimmed,
// the equipped one boxed in yellow. Makes each player's available weapons clear.
void DrawWeaponBar(Player p, string[] keys, float cx, float y, float size)
{
    float pad = size * 0.9f;
    var widths = new float[weapons.Length];
    float total = 0;
    string Label(int i) => $"{(keys[i].Length > 0 ? keys[i] + " " : "")}{WeaponName(i)}{(p.Owned[i] && weapons[i].MagSize > 0 ? " " + p.Ammo[i] : "")}";
    for (int i = 0; i < weapons.Length; i++)
    {
        widths[i] = Raylib.MeasureTextEx(uiFontSmall, Label(i), size, 1).X;
        total += widths[i] + pad;
    }
    total -= pad;
    float x = cx - total / 2f;
    for (int i = 0; i < weapons.Length; i++)
    {
        bool cur = p.Weapon == i;
        bool empty = p.Owned[i] && weapons[i].MagSize > 0 && p.Ammo[i] <= 0;
        Color col = cur ? Color.Yellow
            : !p.Owned[i] ? new Color(105, 105, 110, 255)       // not yet found
            : empty ? new Color(220, 90, 90, 255)               // owned but out of ammo
            : Color.RayWhite;
        if (cur) Raylib.DrawRectangle((int)(x - 5), (int)(y - 3), (int)(widths[i] + 10), (int)(size + 7), new Color(190, 160, 0, 90));
        Raylib.DrawTextEx(uiFontSmall, Label(i), new Vector2(x, y), size, 1, col);
        x += widths[i] + pad;
    }
}

// A coloured HP bar (green→red) with the value overlaid, for the controlling player.
void DrawHpBar(float x, float y, float width, float height, float hp, float maxHp)
{
    float frac = Math.Clamp(hp / Math.Max(1f, maxHp), 0f, 1f);
    Raylib.DrawRectangle((int)x, (int)y, (int)width, (int)height, new Color(0, 0, 0, 170));
    Raylib.DrawRectangle((int)x, (int)y, (int)(width * frac), (int)height, new Color((byte)(220 * (1 - frac) + 30), (byte)(70 + 165 * frac), (byte)55, (byte)255));
    Raylib.DrawRectangleLines((int)x, (int)y, (int)width, (int)height, new Color(255, 255, 255, 70));
    string txt = $"HP {Math.Round(hp)}/{Math.Round(maxHp)}";
    Vector2 m = Raylib.MeasureTextEx(uiFontSmall, txt, height - 8, 1);
    Raylib.DrawTextEx(uiFontSmall, txt, new Vector2(x + 8, y + (height - m.Y) / 2), height - 8, 1, Color.White);
}

void DrawTankStatus(Player p, Tank t, float x, float y, string key)
{
    if (p.InTank && t.Alive)
        DrawTextC(T($"坦克 {Math.Round(Math.Max(0, t.Hp))}/{Math.Round(t.MaxHp)}  ({key} 下車)", $"Tank {Math.Round(Math.Max(0, t.Hp))}/{Math.Round(t.MaxHp)}  ({key} exit)"), x, y, 22, Color.Lime, false);
    else if (!p.InTank && t.Alive && Vector3.Distance(p.Pos, t.Pos) < 5f)
        DrawTextC(T($"{key} 進入坦克", $"{key} enter tank"), x, y, 22, Color.Gold, false);
}

void DrawSplitHud()
{
    int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
    DrawSplitPanelHud(player, playerTank, playerName, p1Kills, p2Kills, new Rectangle(0, 0, w / 2, h), false, "F");
    DrawSplitPanelHud(player2, player2Tank, player2Name, p2Kills, p1Kills, new Rectangle(w / 2, 0, w / 2, h), true, "右Alt");
    string status = winCond == WinCondition.Time
        ? $"{(int)matchTimer / 60}:{(int)matchTimer % 60:00}"
        : T($"目標 {winScore} 殺", $"To {winScore} kills");
    DrawTextC(status, 0, 12, 30, Color.Yellow, true);
}

void DrawSplitPanelHud(Player p, Tank t, string label, int ownKills, int otherKills, Rectangle vp, bool rightSide, string interactKey)
{
    int cx = (int)(vp.X + vp.Width / 2);
    int cy = (int)(vp.Y + vp.Height / 2);
    if (p.Weapon == SniperIndex && !p.InTank && p.Fov < 30f)
    {
        Raylib.BeginScissorMode((int)vp.X, (int)vp.Y, (int)vp.Width, (int)vp.Height);   // clip to this half
        DrawScope(cx, cy, Math.Min(vp.Width, vp.Height) * 0.42f);
        Raylib.EndScissorMode();
    }
    DrawScreenTracers(vp, rightSide, cx, cy);
    DrawCrosshair(cx, cy);
    DrawDamageArrows(new Vector2(cx, cy), p.Yaw, DmgListFor(p));
    float x = vp.X + 18;
    DrawTextC($"{label}  {ownKills}:{otherKills}", x, 20, 24, Color.White, false);
    string ctrl = interactKey == "F"
        ? T("WASD 移動 · 空白 跳 · 1-6/Q 切換武器 · R 換彈 · F 坦克", "WASD move · Space jump · 1-6/Q weapon · R reload · F tank")
        : T("方向鍵 移動 · 右Ctrl 跳 · 7890-=/End 切換武器 · Backspace 換彈 · 右Alt 坦克", "Arrows move · RCtrl jump · 7890-=/End weapon · Backspace reload · RAlt tank");
    DrawTextC(ctrl, x, 48, 16, new Color(205, 205, 205, 190), false);
    DrawHpBar(x, Raylib.GetScreenHeight() - 58, 220, 28, p.Hp, MaxHp);
    var wep = weapons[p.Weapon];
    string ammo = wep.MagSize == 0 ? "" : $" {p.Ammo[p.Weapon]}/{wep.MagSize}";
    DrawTextC($"{p.Weapon + 1}. {WeaponName(p.Weapon)}{ammo}", vp.X + vp.Width - 260, Raylib.GetScreenHeight() - 54, 22, Color.White, false);
    DrawWeaponBar(p, interactKey == "F" ? P1WeaponLabels : P2WeaponLabels, cx, Raylib.GetScreenHeight() - 28, 16);
    DrawTankStatus(p, t, x, Raylib.GetScreenHeight() - 84, interactKey);
    if (p.Hp <= 0) DrawTextC(T("重生中", "Respawning"), vp.X + vp.Width / 2 - 45, vp.Height / 2 + 50, 28, Color.Orange, false);
}

void DrawScreenTracers(Rectangle vp, bool rightSide, int cx, int cy)
{
    foreach (var t in screenTracers)
    {
        if (t.RightSide != rightSide) continue;
        float alpha = Math.Clamp(t.Life / 0.09f, 0, 1);
        Vector2 start = new(vp.X + vp.Width * 0.64f, vp.Y + vp.Height * 0.70f);
        Vector2 end = new(cx, cy);
        Raylib.DrawLineEx(start, end, 7, Raylib.Fade(Color.Black, alpha * 0.75f));
        Raylib.DrawLineEx(start, end, 3, Raylib.Fade(t.Color, alpha));
        Raylib.DrawCircleV(start, 5, Raylib.Fade(t.Color, alpha));
    }
}

void DrawDamageArrows(Vector2 center, float viewerYaw, List<DamageInd> list)
{
    foreach (var d in list)
    {
        float rel = d.WorldAngle - viewerYaw;
        float op = Math.Clamp(d.Life / 1.5f, 0f, 1f);
        Vector2 dir = new(MathF.Sin(rel), -MathF.Cos(rel));
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 tip = center + dir * 134;
        Vector2 b1 = center + dir * 98 + perp * 26;
        Vector2 b2 = center + dir * 98 - perp * 26;
        Color c = Raylib.Fade(new Color(255, 50, 50, 255), op);
        Raylib.DrawTriangle(tip, b1, b2, c);   // both windings so it isn't backface-culled
        Raylib.DrawTriangle(tip, b2, b1, c);
    }
}

// Sniper scope vignette: black mask everywhere outside a central circle, with a thin
// ring and fine crosshair. Drawn over the (already zoomed) view.
void DrawScope(float cx, float cy, float radius)
{
    Raylib.DrawRing(new Vector2(cx, cy), radius, radius + 4000, 0, 360, 64, new Color(0, 0, 0, 240));
    Raylib.DrawCircleLines((int)cx, (int)cy, radius, new Color(0, 0, 0, 255));
    Raylib.DrawCircleLines((int)cx, (int)cy, radius - 2, new Color(255, 255, 255, 45));
    Raylib.DrawLineEx(new Vector2(cx - radius, cy), new Vector2(cx + radius, cy), 1, new Color(0, 0, 0, 170));
    Raylib.DrawLineEx(new Vector2(cx, cy - radius), new Vector2(cx, cy + radius), 1, new Color(0, 0, 0, 170));
    Raylib.DrawCircle((int)cx, (int)cy, 2, Color.Red);
}

void DrawCrosshair(int cx, int cy)
{
    Raylib.DrawRectangle(cx - 18, cy - 3, 36, 6, Color.Black);
    Raylib.DrawRectangle(cx - 3, cy - 18, 6, 36, Color.Black);
    Raylib.DrawRectangle(cx - 14, cy - 1, 28, 2, Color.White);
    Raylib.DrawRectangle(cx - 1, cy - 14, 2, 28, Color.White);
    Raylib.DrawCircle(cx, cy, 3, Color.Black);
    Raylib.DrawCircle(cx, cy, 1.5f, Color.Yellow);
}

void DrawCenterPanel(string title, string sub)
{
    int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
    Raylib.DrawRectangle(0, 0, w, h, new Color(0, 0, 0, 150));
    DrawTextC(title, 0, h / 2 - 70, 54, Color.Yellow, true);
    DrawTextC(sub, 0, h / 2 + 5, 24, Color.White, true);
}

void DrawTextC(string text, float x, float y, float size, Color color, bool centered)
{
    EnsureFontForText(text);
    Font f = size <= 40 ? uiFontSmall : uiFontBig;
    Vector2 pos = new(x, y);
    if (centered)
    {
        Vector2 m = Raylib.MeasureTextEx(f, text, size, 1);
        pos.X = Raylib.GetScreenWidth() / 2f - m.X / 2f;
    }
    Raylib.DrawTextEx(f, text, pos, size, 1, color);
}

// Returns usable single-face TrueType bytes for a font file, unpacking .ttc if needed.
byte[]? LoadUiFontData(string path)
{
    try
    {
        byte[] raw = File.ReadAllBytes(path);
        if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) return ExtractTtcFace(raw, 0);
        return raw;
    }
    catch { return null; }
}

// Build a standalone .ttf from face `index` of a .ttc collection. raylib/stb_truetype
// loads a .ttc at offset 0 (the collection header), which yields no glyphs, so we
// rewrite the chosen face's sfnt table directory into its own file.
byte[]? ExtractTtcFace(byte[] ttc, int index)
{
    if (ttc.Length < 16 || ttc[0] != (byte)'t' || ttc[1] != (byte)'t' || ttc[2] != (byte)'c' || ttc[3] != (byte)'f') return null;
    uint numFonts = BE32(ttc, 8);
    if (index < 0 || index >= numFonts) return null;
    int dirOff = (int)BE32(ttc, 12 + index * 4);
    if (dirOff + 12 > ttc.Length) return null;
    uint sfntVer = BE32(ttc, dirOff);
    int numTables = BE16(ttc, dirOff + 4);
    int recStart = dirOff + 12;
    if (recStart + numTables * 16 > ttc.Length) return null;

    int headerSize = 12 + numTables * 16;
    int total = headerSize;
    var padLen = new int[numTables];
    for (int i = 0; i < numTables; i++)
    {
        uint len = BE32(ttc, recStart + i * 16 + 12);
        padLen[i] = ((int)len + 3) & ~3;
        total += padLen[i];
    }
    var outb = new byte[total];
    WBE32(outb, 0, sfntVer);
    WBE16(outb, 4, numTables);
    int pow = 1, sel = 0;
    while (pow * 2 <= numTables) { pow *= 2; sel++; }
    WBE16(outb, 6, pow * 16);
    WBE16(outb, 8, sel);
    WBE16(outb, 10, numTables * 16 - pow * 16);
    int dataPos = headerSize;
    for (int i = 0; i < numTables; i++)
    {
        int srcRec = recStart + i * 16, dstRec = 12 + i * 16;
        uint off = BE32(ttc, srcRec + 8), len = BE32(ttc, srcRec + 12);
        WBE32(outb, dstRec, BE32(ttc, srcRec));      // tag
        WBE32(outb, dstRec + 4, BE32(ttc, srcRec + 4)); // checksum
        WBE32(outb, dstRec + 8, (uint)dataPos);      // new offset
        WBE32(outb, dstRec + 12, len);
        if (off + len <= ttc.Length) Array.Copy(ttc, (int)off, outb, dataPos, (int)len);
        dataPos += padLen[i];
    }
    return outb;
}

static uint BE32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
static int BE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
static void WBE32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
static void WBE16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

// A simple directional-light shader: each fragment is shaded by its face normal
// against a fixed "sun" direction, so the six sides of every box differ in tone.
// Primitives without a meaningful normal (lines) fall back to full brightness.
Shader LoadLightShader()
{
    string vs = "#version 330\n"
        + "in vec3 vertexPosition; in vec3 vertexNormal; in vec4 vertexColor;\n"
        + "uniform mat4 mvp; out vec3 fNormal; out vec4 fColor;\n"
        + "void main(){ fNormal = vertexNormal; fColor = vertexColor; gl_Position = mvp*vec4(vertexPosition,1.0); }";
    string fs = "#version 330\n"
        + "in vec3 fNormal; in vec4 fColor; uniform vec4 colDiffuse;\n"
        + "uniform vec3 sunDir; uniform float ambient; out vec4 finalColor;\n"
        + "void main(){ float L=length(fNormal);\n"
        + "  float lit = (L>0.001) ? ambient + (1.0-ambient)*max(dot(fNormal/L, -normalize(sunDir)),0.0) : 1.0;\n"
        + "  finalColor = vec4(fColor.rgb*colDiffuse.rgb*lit, fColor.a*colDiffuse.a); }";
    Shader sh = LoadShaderMem(vs, fs);
    Vector3 sun = Vector3.Normalize(new Vector3(-0.55f, -1.0f, -0.4f));
    Raylib.SetShaderValue(sh, ShaderLoc(sh, "sunDir"), sun, ShaderUniformDataType.Vec3);
    Raylib.SetShaderValue(sh, ShaderLoc(sh, "ambient"), 0.32f, ShaderUniformDataType.Float);
    return sh;
}

Shader LoadShaderMem(string vs, string fs)
{
    byte[] v = Encoding.UTF8.GetBytes(vs + "\0");
    byte[] f = Encoding.UTF8.GetBytes(fs + "\0");
    unsafe { fixed (byte* vp = v) fixed (byte* fp = f) return Raylib.LoadShaderFromMemory((sbyte*)vp, (sbyte*)fp); }
}

int ShaderLoc(Shader s, string name)
{
    byte[] b = Encoding.ASCII.GetBytes(name + "\0");
    unsafe { fixed (byte* p = b) return Raylib.GetShaderLocation(s, (sbyte*)p); }
}

Font LoadChineseFont(int baseSize)
{
    if (uiFontData == null) return Raylib.GetFontDefault();
    try
    {
        // Bake near the on-screen size (two buckets) so strokes aren't lost to heavy
        // minification, and filter bilinearly.
        int[] cps = uiCodepoints.OrderBy(x => x).ToArray();
        byte[] ftype = Encoding.ASCII.GetBytes(".ttf\0");
        Font f;
        unsafe
        {
            fixed (byte* data = uiFontData)
            fixed (int* cp = cps)
            fixed (byte* ft = ftype)
            {
                f = Raylib.LoadFontFromMemory((sbyte*)ft, data, uiFontData.Length, baseSize, cp, cps.Length);
            }
        }
        Raylib.SetTextureFilter(f.Texture, TextureFilter.Bilinear);
        uiFontCustom = true;
        return f;
    }
    catch
    {
        uiFontCustom = false;
        return Raylib.GetFontDefault();
    }
}

void EnsureFontForText(string text)
{
    if (uiFontPath == null) return;
    int before = uiCodepoints.Count;
    AddCodepoints(text);
    if (uiCodepoints.Count == before) return;
    if (uiFontCustom) { Raylib.UnloadFont(uiFontSmall); Raylib.UnloadFont(uiFontBig); }
    uiFontSmall = LoadChineseFont(UiFontSmallBase);
    uiFontBig = LoadChineseFont(UiFontBigBase);
}

void AddCodepoints(string text)
{
    for (int i = 32; i <= 126; i++) uiCodepoints.Add(i);
    uiCodepoints.Add(0x3000);
    foreach (var rune in text.EnumerateRunes())
    {
        int v = rune.Value;
        if (v >= 32) uiCodepoints.Add(v);
    }
}

enum AppState { Start, SplitNames, HostLobby, JoinLobby, LocalMouseSelect, Playing, Paused, GameOver }
enum WinCondition { Score, Time }
enum GameMode { Single, LocalSplit, LanHost, LanClient }
enum EnemyType { Shooter, Brawler, Tank }

record ScoreEntry(int Score, int Wave, bool Victory, string Name);
record Weapon(string Name, int MagSize, float Damage, float Cooldown, float Range, bool Explosive, float Spread, bool FullAuto, bool Melee);
record Obstacle(BoundingBox Box, bool Destructible);
record HitResult(Enemy? Enemy, bool Remote, Vector3 Point);

struct PlayerInput
{
    public float LookDX, LookDY;
    public bool Fwd, Back, MoveLeft, MoveRight, Sprint, Jump, Interact, Fire, FirePressed, Reload, NextWeapon, Aim;
    public int WeaponSelect;
}

struct FireIntent
{
    public bool Fire;
    public Vector3 Eye, Dir, Right;
}

class Player
{
    public Vector3 Pos = new(0, 1.7f, 0);
    const float DefaultMaxHp = 180f;
    public float Yaw, Pitch, Hp = DefaultMaxHp, Cooldown, SwordAnim, VelY, HitFlash;
    public int Weapon;
    public int[] Ammo;
    public bool[] Owned;
    public bool OnGround = true, PrevJump, InTank, PrevF;
    public float Fov = 75f;   // current camera FOV; drops when aiming (sniper zoom)
    public Player(Weapon[] weapons) { Ammo = weapons.Select(w => w.MagSize).ToArray(); Owned = PistolOnly(weapons); }
    static bool[] PistolOnly(Weapon[] weapons) { var o = new bool[weapons.Length]; o[0] = true; o[^1] = true; return o; }   // pistol + fists
    public void Reset(Weapon[] weapons)
    {
        Pos = new Vector3(0, 1.7f, 0);
        Yaw = Pitch = Cooldown = SwordAnim = VelY = HitFlash = 0;
        Hp = DefaultMaxHp;
        Weapon = 0;
        OnGround = true; PrevJump = InTank = PrevF = false;
        Fov = 75f;
        Ammo = weapons.Select(w => w.MagSize).ToArray();
        Owned = PistolOnly(weapons);
    }
}

class Tank
{
    public Vector3 Pos;
    public float Yaw, Hp = 600, MaxHp = 600, FireCd, SmashCd;
    public bool Alive = true;
    public void Reset(Vector3 pos, float yaw)
    {
        Pos = pos; Yaw = yaw; Hp = MaxHp; FireCd = SmashCd = 0; Alive = true;
    }
}

class Ally
{
    public Vector3 Pos;
    public float Yaw, Hp = 170, MaxHp = 170, FireCd, Respawn;
    public bool Alive = true;
}

class RemotePlayer
{
    public string Name = "對手";
    public Vector3 Pos = new(0, 1.7f, 0);
    public float Yaw, Pitch, Hp = 180f;
    public int Weapon;
    public bool Alive, InTank;
}

class Enemy
{
    public Vector3 Pos;
    public float Hp, MaxHp, AttackCd, HitFlash;
    public EnemyType Type;
    public bool Alive;
    public float Radius => Type == EnemyType.Tank ? 1.8f : .55f;
    public Vector3 Center => Pos + new Vector3(0, Type == EnemyType.Tank ? 1.2f : 1.1f, 0);
    public BoundingBox Bounds => Type == EnemyType.Tank
        ? new BoundingBox(Pos + new Vector3(-1.8f, 0, -2.1f), Pos + new Vector3(1.8f, 2.4f, 2.1f))
        : new BoundingBox(Pos + new Vector3(-.55f, 0, -.55f), Pos + new Vector3(.55f, 2.3f, .55f));
}

class DamageInd { public float WorldAngle; public float Life; }
class Flash { public Vector3 Pos; public float Life; }
class Bullet { public Vector3 Pos, Dir; public float Speed, Damage, Life; }
class Rocket { public Vector3 Pos, Dir; public float Life, Damage, Radius; public int Owner = -1; public bool FromTank; }
class Tracer { public Vector3 From, To; public float Life; public Color Color; }
class ScreenTracer { public bool RightSide; public float Life; public Color Color; }
class Pickup { public Vector3 Pos; public bool Active; public float Respawn; }
class WeaponCrate { public Vector3 Pos; public int Weapon; public bool Active; public float Respawn; }
class HostInfo
{
    public IPAddress Address;
    public int Port;
    public string Name;
    public DateTime LastSeen;
    public HostInfo(IPAddress address, int port, string name, DateTime lastSeen) { Address = address; Port = port; Name = name; LastSeen = lastSeen; }
}

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

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    static extern uint GetRawInputDeviceNameW(IntPtr hDevice, uint uiCommand, System.Text.StringBuilder? pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, byte[] preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [In, Out] ushort[] usageList, ref uint usageLength, byte[] preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsedData);

    const int GWLP_WNDPROC = -4;
    const uint WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

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
        public int Index;
        public IntPtr Handle;
        public string Group = "";   // physical-device key (collapses a mouse's multiple HID handles into one)
        public bool IsTouch;
        public long MoveTotal;
        public int Clicks;
        public bool LeftDown, RightDown, LeftPressedThisFrame, JustClicked;
        public long SelMove;   // movement accumulated since the select screen opened (wiggle-to-assign)
        // Precision-touchpad relative-motion tracking (digitizer reports absolute X/Y).
        public int LastX, LastY;
        public bool HasLast, TipDown;
        public long TouchStartMs, TouchMove;
        public (int dx, int dy) ConsumeDelta()
        {
            var r = (_dx, _dy);
            _dx = 0; _dy = 0;
            return r;
        }
        public void Accumulate(int dx, int dy) { _dx += dx; _dy += dy; long m = Math.Abs(dx) + Math.Abs(dy); MoveTotal += m; SelMove += m; }
    }

    public static Dictionary<IntPtr, DeviceState> Devices = new();
    static Dictionary<IntPtr, byte[]?> _preparsed = new();
    static int _nextIndex;
    public static bool IsRegistered { get; private set; }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    static WndProcDelegate? _wndProcDelegate;
    static IntPtr _oldWndProc;
    static byte[] _buffer = new byte[1024];

    public static bool Initialize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // Try mouse + precision-touchpad; fall back to mouse only if the touchpad usage is rejected.
            if (!Register(hwnd, true) && !Register(hwnd, false)) return false;
            _wndProcDelegate = MyWndProc;
            IntPtr newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProc);
            if (_oldWndProc == IntPtr.Zero) return false;
            IsRegistered = true;
            return true;
        }
        catch { return false; }
    }

    static bool Register(IntPtr hwnd, bool withTouch)
    {
        int n = withTouch ? 2 : 1;
        var arr = new RAWINPUTDEVICE[2];
        arr[0] = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }; // mouse
        arr[1] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }; // precision touchpad
        int devSize = Marshal.SizeOf<RAWINPUTDEVICE>();
        IntPtr ptr = Marshal.AllocHGlobal(devSize * n);
        try
        {
            for (int i = 0; i < n; i++) Marshal.StructureToPtr(arr[i], ptr + i * devSize, false);
            return RegisterRawInputDevices(ptr, (uint)n, (uint)devSize);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static IntPtr MyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) ProcessInput(lParam);
        return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    static DeviceState GetOrAdd(IntPtr h, bool touch)
    {
        if (!Devices.TryGetValue(h, out var dev))
        {
            dev = new DeviceState { Index = _nextIndex++, Handle = h, IsTouch = touch, Group = GroupKey(h) };
            Devices[h] = dev;
        }
        if (touch) dev.IsTouch = true;
        return dev;
    }

    // A key identifying the physical device. Many mice expose several Raw Input handles
    // (one for movement, one for buttons); they share a device name that differs only by
    // the &ColNN / &MI_NN collection token, so stripping those groups them together.
    static string GroupKey(IntPtr h)
    {
        try
        {
            uint size = 0;
            GetRawInputDeviceNameW(h, RIDI_DEVICENAME, null, ref size);   // size in characters
            if (size == 0 || size > 4000) return h.ToString();
            var sb = new System.Text.StringBuilder((int)size + 1);
            if (GetRawInputDeviceNameW(h, RIDI_DEVICENAME, sb, ref size) == 0xFFFFFFFF) return h.ToString();
            string s = sb.ToString().ToLowerInvariant();
            int g = s.LastIndexOf('#');                                   // drop the interface {guid}
            if (g > 0) s = s.Substring(0, g);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"&(col\d+|mi_\d+)", "");
            return string.IsNullOrEmpty(s) ? h.ToString() : s;
        }
        catch { return h.ToString(); }
    }

    public static bool SameGroup(IntPtr a, IntPtr b)
        => a == b || (Devices.TryGetValue(a, out var da) && Devices.TryGetValue(b, out var db) && da.Group == db.Group);

    // Aggregate movement + buttons across every handle of the physical device that owns
    // `anyHandle`, so the assigned (click) handle and the (separate) movement handle act
    // as one mouse. Consumes the deltas/pressed-flags it reads.
    public static bool TryReadGroup(IntPtr anyHandle, out int dx, out int dy, out bool leftDown, out bool leftPressed, out bool rightDown)
    {
        dx = dy = 0; leftDown = leftPressed = rightDown = false;
        if (anyHandle == IntPtr.Zero || !Devices.TryGetValue(anyHandle, out var anchor)) return false;
        foreach (var d in Devices.Values)
        {
            if (d.Group != anchor.Group) continue;
            var (ddx, ddy) = d.ConsumeDelta();
            dx += ddx; dy += ddy;
            leftDown |= d.LeftDown;
            leftPressed |= d.LeftPressedThisFrame;
            rightDown |= d.RightDown;
        }
        return true;
    }

    static void ProcessInput(IntPtr lParam)
    {
        uint size = (uint)_buffer.Length;
        uint result;
        unsafe
        {
            fixed (byte* p = _buffer)
            {
                result = GetRawInputData(lParam, RID_INPUT, (IntPtr)p, ref size, 24);
            }
        }
        if (result == 0 || result == 0xFFFFFFFF) return;
        uint dwType = BitConverter.ToUInt32(_buffer, 0);
        IntPtr hDevice = new(BitConverter.ToInt64(_buffer, 8));
        if (dwType == 0) // RIM_TYPEMOUSE
        {
            if (result < 48) return;
            ushort flags = BitConverter.ToUInt16(_buffer, 28);
            int dx = BitConverter.ToInt32(_buffer, 36);
            int dy = BitConverter.ToInt32(_buffer, 40);
            var dev = GetOrAdd(hDevice, false);
            dev.Accumulate(dx, dy);
            if ((flags & 0x0001) != 0) { dev.LeftDown = true; dev.LeftPressedThisFrame = true; dev.JustClicked = true; dev.Clicks++; }
            if ((flags & 0x0002) != 0) dev.LeftDown = false;
            if ((flags & 0x0004) != 0) { dev.RightDown = true; dev.JustClicked = true; }
            if ((flags & 0x0008) != 0) dev.RightDown = false;
        }
        else if (dwType == 2 && result >= 32) // RIM_TYPEHID (precision touchpad)
        {
            ParseTouch(hDevice, result);
        }
    }

    // Parse a precision-touchpad HID report: read the first contact's absolute X/Y and
    // turn it into a relative delta, plus tip-switch / click-button state.
    static void ParseTouch(IntPtr hDevice, uint result)
    {
        try
        {
            uint sizeHid = BitConverter.ToUInt32(_buffer, 24);
            uint count = BitConverter.ToUInt32(_buffer, 28);
            if (sizeHid == 0 || count == 0) return;
            int rlen = (int)Math.Min(Math.Min(sizeHid, result - 32), (uint)(_buffer.Length - 32));
            if (rlen <= 0) return;
            byte[] report = new byte[rlen];
            Array.Copy(_buffer, 32, report, 0, rlen);
            byte[]? pre = GetPreparsed(hDevice);
            if (pre == null) return;

            bool tip = HasUsage(pre, report, 0x0D, 0x42);   // finger touching
            bool button = HasUsage(pre, report, 0x09, 0x01); // physical click-pad button
            bool gotX = HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x30, out uint xv, pre, report, (uint)rlen) == HIDP_STATUS_SUCCESS;
            bool gotY = HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x31, out uint yv, pre, report, (uint)rlen) == HIDP_STATUS_SUCCESS;

            var dev = GetOrAdd(hDevice, true);
            long now = Environment.TickCount64;
            if (gotX && gotY && tip)
            {
                if (!dev.TipDown) { dev.TouchStartMs = now; dev.TouchMove = 0; dev.HasLast = false; }
                if (dev.HasLast)
                {
                    int ddx = (int)xv - dev.LastX, ddy = (int)yv - dev.LastY;
                    dev.Accumulate(ddx, ddy);
                    dev.TouchMove += Math.Abs(ddx) + Math.Abs(ddy);
                }
                dev.LastX = (int)xv; dev.LastY = (int)yv; dev.HasLast = true;
            }
            // Click-pad button = held left mouse button.
            if (button && !dev.LeftDown) { dev.LeftPressedThisFrame = true; dev.JustClicked = true; dev.Clicks++; }
            // Tap-to-click: a brief, low-movement touch release counts as a left click.
            if (dev.TipDown && !tip && now - dev.TouchStartMs < 300 && dev.TouchMove < 40)
            {
                dev.LeftPressedThisFrame = true; dev.JustClicked = true; dev.Clicks++;
            }
            dev.LeftDown = button;
            dev.TipDown = tip;
        }
        catch { }
    }

    static bool HasUsage(byte[] pre, byte[] report, ushort page, ushort usage)
    {
        uint max = HidP_MaxUsageListLength(HidP_Input, page, pre);
        if (max == 0 || max > 256) return false;
        var list = new ushort[max];
        uint n = max;
        if (HidP_GetUsages(HidP_Input, page, 0, list, ref n, pre, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) return false;
        for (int i = 0; i < n; i++) if (list[i] == usage) return true;
        return false;
    }

    static byte[]? GetPreparsed(IntPtr h)
    {
        if (_preparsed.TryGetValue(h, out var cached)) return cached;
        uint size = 0;
        GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        byte[]? data = null;
        if (size > 0 && size < 1_000_000)
        {
            data = new byte[size];
            var gh = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                if (GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, gh.AddrOfPinnedObject(), ref size) == 0xFFFFFFFF) data = null;
            }
            finally { gh.Free(); }
        }
        _preparsed[h] = data;
        return data;
    }

    public static void NewFrame()
    {
        foreach (var d in Devices.Values) d.LeftPressedThisFrame = false;
    }

    // Reset the wiggle/click accumulators when the device-assignment screen opens.
    public static void BeginSelect()
    {
        foreach (var d in Devices.Values) { d.SelMove = 0; d.JustClicked = false; d.LeftPressedThisFrame = false; }
    }
}
