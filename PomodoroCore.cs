using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoffeeViewer;

// ───────── 뽀모도로 위젯의 데이터 · 타이머 · 소리 (PomodoroTimer 앱을 WinForms로 옮긴 것) ─────────

internal sealed class PomoTimerDef
{
    public string Name { get; set; } = "New Timer";
    public int Duration { get; set; } = 25;           // 분
    public int Seconds { get; set; }                  // 초(0~59)
    [JsonIgnore] public int TotalSeconds => Duration * 60 + Seconds;
    public string Color { get; set; } = "#808080";
    public bool SoundEnabled { get; set; }
    public List<string> SoundPaths { get; set; } = new();
    public string PlayMode { get; set; } = "shuffle"; // shuffle | loop

    public Color ColorValue
    {
        get { try { return ColorTranslator.FromHtml(Color); } catch { return System.Drawing.Color.Gray; } }
    }
}

internal sealed class PomoTodo
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

internal sealed class PomoSettings
{
    public List<PomoTimerDef> Timers { get; set; } = new();
    public bool AutoAdvance { get; set; }
    public bool ShowDesktopNotification { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public string OverlayDrainDirection { get; set; } = "right"; // right | left
    public double Volume { get; set; } = 1;
    public List<PomoTodo> Todos { get; set; } = new();
    public int CurrentMode { get; set; }
    [JsonIgnore] public bool WidgetShown { get; set; } // 저장하지 않는다: 프로그램을 켜거나 에피소드에 들어갈 때 자동으로 뜨지 않고, 사이드바 아이콘으로만 켠다
    public bool Mini { get; set; } public bool Locked { get; set; }
    public int PosX { get; set; } = -1;
    public int PosY { get; set; } = -1;

    public static List<PomoTimerDef> DefaultTimers() => new()
    {
        new PomoTimerDef { Name = "Work Time", Duration = 25, Color = "#7c3aed" },
        new PomoTimerDef { Name = "Short Break", Duration = 5, Color = "#808080" },
    };
}

internal enum PomoState { Idle, Running, Paused }

/// <summary>타이머 하나(앱 전체에서 하나). 위젯이 닫히거나 작품을 옮겨도 계속 돈다.</summary>
internal static class Pomodoro
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(AppSettings.DataDir, "pomodoro.json");

    public static PomoSettings S { get; private set; } = Load();

    public static PomoState State { get; private set; } = PomoState.Idle;
    public static double RemainingMs { get; private set; }
    public static double TotalMs { get; private set; }

    public static event Action? Tick;
    public static event Action? StateChanged;
    public static event Action<string>? Banner;

    private static readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private static long targetTick;
    private static readonly PomoAudio audio = new();
    private static bool wired;

    public static PomoTimerDef Current => S.Timers[Math.Clamp(S.CurrentMode, 0, S.Timers.Count - 1)];

    public static void Init()
    {
        if (wired) return;
        wired = true;
        timer.Tick += (_, _) => OnTick();
        audio.ShouldContinue = () => State == PomoState.Running;
        audio.Error += m => Banner?.Invoke(m);
        audio.Volume = S.Volume;
        Reset();
    }

    // ── 저장 ──
    private static PomoSettings Load()
    {
        PomoSettings? s = null;
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<PomoSettings>(File.ReadAllText(FilePath), Opts); } catch { }
        return Normalize(s ?? new PomoSettings());
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(S, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    private static PomoSettings Normalize(PomoSettings s)
    {
        s.Timers ??= new();
        s.Timers.RemoveAll(t => t == null);
        s.Todos ??= new();
        s.Todos.RemoveAll(t => t == null);
        foreach (var t in s.Todos) t.Text ??= "";
        if (s.Timers.Count == 0) s.Timers = PomoSettings.DefaultTimers();
        foreach (var t in s.Timers)
        {
            t.Name ??= ""; t.Color ??= "";
            t.SoundPaths ??= new();
            t.Duration = Math.Clamp(t.Duration, 0, 999);
            t.Seconds = Math.Clamp(t.Seconds, 0, 59);
            if (t.TotalSeconds < 1) t.Seconds = 1;
            if (t.PlayMode != "loop") t.PlayMode = "shuffle";
        }
        if (s.OverlayDrainDirection != "left") s.OverlayDrainDirection = "right";
        s.Volume = Math.Clamp(double.IsNaN(s.Volume) ? 1 : s.Volume, 0, 1);
        s.CurrentMode = Math.Clamp(s.CurrentMode, 0, s.Timers.Count - 1);
        return s;
    }

    // ── 조작 ──
    public static void Toggle()
    {
        switch (State)
        {
            case PomoState.Idle: StartCurrent(); break;
            case PomoState.Running: Pause(); break;
            case PomoState.Paused: Resume(); break;
        }
    }

    private static void StartCurrent()
    {
        TotalMs = Current.TotalSeconds * 1000.0;
        RemainingMs = TotalMs;
        targetTick = Environment.TickCount64 + (long)TotalMs;
        State = PomoState.Running;
        timer.Start();
        audio.Play(Current);
        StateChanged?.Invoke();
    }

    private static void Pause()
    {
        RemainingMs = Math.Max(0, targetTick - Environment.TickCount64);
        State = PomoState.Paused;
        timer.Stop();
        audio.Pause();
        StateChanged?.Invoke();
    }

    private static void Resume()
    {
        targetTick = Environment.TickCount64 + (long)RemainingMs;
        State = PomoState.Running;
        timer.Start();
        audio.Play(Current);
        StateChanged?.Invoke();
    }

    public static void Reset()
    {
        timer.Stop();
        audio.Stop();
        State = PomoState.Idle;
        TotalMs = RemainingMs = Current.TotalSeconds * 1000.0;
        StateChanged?.Invoke();
        Tick?.Invoke();
    }

    public static void CycleMode()
    {
        if (State != PomoState.Idle) { Banner?.Invoke("리셋 후 전환할 수 있어요"); return; }
        S.CurrentMode = (S.CurrentMode + 1) % S.Timers.Count;
        Save();
        Reset();
    }

    /// <summary>설정 창에서 목록이 바뀐 뒤: 지금 쓰는 타이머를 같은 항목으로 이어 가고, 멈춰 있으면 새 길이로 맞춘다.</summary>
    public static void SettingsChanged(PomoTimerDef? keep)
    {
        Normalize(S);
        if (keep != null && S.Timers.Contains(keep)) S.CurrentMode = S.Timers.IndexOf(keep);
        audio.Volume = S.Volume;
        Save();
        if (State == PomoState.Idle) Reset(); else StateChanged?.Invoke();
    }

    public static void SetVolume(double v)
    {
        S.Volume = Math.Clamp(v, 0, 1);
        audio.Volume = S.Volume;
        Save();
    }

    private static void OnTick()
    {
        if (State != PomoState.Running) return;
        RemainingMs = Math.Max(0, targetTick - Environment.TickCount64);
        Tick?.Invoke();
        if (RemainingMs > 0) return;
        timer.Stop();
        Completed();
    }

    private static void Completed()
    {
        var done = Current;
        audio.Stop();
        if (S.PlaySound) PomoBeep.Play();
        if (S.ShowDesktopNotification) Notify($"{done.Name} 완료!");
        S.CurrentMode = (S.CurrentMode + 1) % S.Timers.Count;
        Save();
        Reset();
        Banner?.Invoke($"✓ {done.Name} 완료");
        if (S.AutoAdvance)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += (_, _) => { t.Dispose(); if (State == PomoState.Idle) StartCurrent(); };
            t.Start();
        }
    }

    private static void Notify(string text)
    {
        try
        {
            var ni = new NotifyIcon { Icon = AppShared.LoadAppIcon() ?? SystemIcons.Information, Visible = true };
            ni.ShowBalloonTip(5000, "Caffeum 타이머", text, ToolTipIcon.Info);
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += (_, _) => { t.Dispose(); ni.Visible = false; ni.Dispose(); };
            t.Start();
        }
        catch { }
    }
}

/// <summary>완료 알림음: 800Hz → 0.2초 뒤 1200Hz, 1초 동안 감쇠.</summary>
internal static class PomoBeep
{
    private static System.Media.SoundPlayer? player;

    public static void Play()
    {
        try { player ??= new System.Media.SoundPlayer(new MemoryStream(Build())); player.Play(); }
        catch { }
    }

    private static byte[] Build()
    {
        const int rate = 44100, n = rate;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double freq = t < 0.2 ? 800 : 1200;
            double amp = 0.5 * Math.Pow(0.02, t);
            phase += 2 * Math.PI * freq / rate;
            w.Write((short)(Math.Sin(phase) * amp * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>타이머별 배경 음악. WPF 없이 윈도우 MCI로 재생한다(mp3 · wav · wma · m4a · mp4 소리). 끝나면 다음 곡(섞기/차례대로).</summary>
internal sealed class PomoAudio
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string cmd, StringBuilder? ret, int retLen, IntPtr hwnd);

    private readonly Random rng = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 500 };
    private const string Alias = "pomobgm";
    private PomoTimerDef? def;
    private bool open, paused;
    private int seq, fails;
    private string? lastPick;
    private double volume = 1;

    public Func<bool> ShouldContinue { get; set; } = () => true;
    public event Action<string>? Error;

    public PomoAudio() { poll.Tick += (_, _) => CheckEnded(); }

    public double Volume
    {
        get => volume;
        set { volume = value; if (open) SetVol(); }
    }

    public void Play(PomoTimerDef d)
    {
        if (!d.SoundEnabled || d.SoundPaths.Count == 0) { Stop(); return; }
        if (open && def == d) { if (paused) { Mci($"play {Alias}"); paused = false; } poll.Start(); return; }
        Stop();
        def = d; seq = 0; fails = 0;
        PlayNext();
    }

    public void Pause()
    {
        if (!open) return;
        Mci($"pause {Alias}");
        paused = true;
        poll.Stop();
    }

    public void Stop()
    {
        poll.Stop();
        CloseTrack();
        def = null;
    }

    private void CloseTrack()
    {
        if (!open) return;
        Mci($"close {Alias}");
        open = false; paused = false;
    }

    private void PlayNext()
    {
        if (def == null) return;
        var paths = def.SoundPaths;
        if (paths.Count == 0 || fails >= paths.Count) { Stop(); return; }
        string pick;
        if (def.PlayMode == "loop")
        {
            if (seq >= paths.Count) seq = 0;
            pick = paths[seq++];
        }
        else
        {
            int i = rng.Next(paths.Count);
            if (paths.Count > 1 && paths[i] == lastPick) i = (i + 1 + rng.Next(paths.Count - 1)) % paths.Count;
            pick = paths[i];
        }
        lastPick = pick;
        CloseTrack();
        if (!File.Exists(pick)) { Fail(pick); return; }
        if (Mci($"open \"{pick}\" type mpegvideo alias {Alias}") != 0) { Fail(pick); return; }
        open = true;
        SetVol();
        if (Mci($"play {Alias}") != 0) { Fail(pick); return; }
        fails = 0;
        poll.Start();
    }

    private void Fail(string path)
    {
        fails++;
        CloseTrack();
        Error?.Invoke($"재생 실패: {Path.GetFileName(path)}");
        if (def != null && ShouldContinue()) PlayNext();
    }

    private void CheckEnded()
    {
        if (!open || paused) return;
        var sb = new StringBuilder(64);
        mciSendString($"status {Alias} mode", sb, sb.Capacity, IntPtr.Zero);
        if (sb.ToString() == "stopped" && ShouldContinue()) PlayNext();
    }

    private void SetVol() => Mci($"setaudio {Alias} volume to {(int)Math.Round(volume * 1000)}");

    private static int Mci(string cmd) => mciSendString(cmd, null, 0, IntPtr.Zero);
}
