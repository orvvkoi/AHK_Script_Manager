using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AHKScriptManager;

public enum RestartReason { None, Crash, FileChanged, TargetStarted, Profile, Manual }

sealed class HotkeyBinding
{
    public Action Action { get; init; } = static () => { };
    public ScriptItem? Script { get; init; }
    public string Owner { get; init; } = "";
}

static class ConfigSchema
{
    public const int Current = 3;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AppConfig))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ScriptItem))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProfileItem))]
internal partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

public sealed class ScriptItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "Auto";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "Default";
    public string StartHotkey { get; set; } = "";
    public string StopHotkey { get; set; } = "";
    public string AhkExe { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    public bool AutoRestart { get; set; }
    public int MaxRestarts { get; set; } = 3;
    public int RestartDelayMs { get; set; } = 3000;
    public int RestartWindowSeconds { get; set; } = 60;
    public bool WatchFile { get; set; }
    public bool Enabled { get; set; } = true;
    public string TargetProcess { get; set; } = "";
    public bool StartWhenTargetStarts { get; set; }
    public bool StopWhenTargetExits { get; set; }
    // When false, the script is controlled only manually (buttons/hotkeys/profiles).
    public bool AutomaticTargetMonitoring { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int? Pid { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastStart { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastFileWrite { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int RestartCount { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool DesiredRunning { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool RestartPending { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime? ProcessStartTimeUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastObservedFileWrite { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long LastObservedFileLength { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long LastFileLength { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime? FileChangePendingSince { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ManualOverride { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime? FileRestartCooldownUntilUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public RestartReason RestartReason { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public List<DateTime> RestartHistoryUtc { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> ActiveProfileIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> ScriptIds { get; set; } = new();
    public int StartDelayMs { get; set; } = 500;
    public int StopDelayMs { get; set; } = 200;
    public string StartHotkey { get; set; } = "";
    public string StopHotkey { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = ConfigSchema.Current;
    public List<ScriptItem> Scripts { get; set; } = new();
    public List<ProfileItem> Profiles { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool PortableMode { get; set; }
    public string EmergencyStopHotkey { get; set; } = "Ctrl+Alt+Pause";
    public string AhkV1Path { get; set; } = "";
    public string AhkV2Path { get; set; } = "";
    public bool StopScriptsOnManagerExit { get; set; }
    public int GracefulStopTimeoutMs { get; set; } = 1200;
}

public sealed class MainForm : Form
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    const uint MOD_NOREPEAT = 0x4000;
    const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    readonly AppConfig cfg = new();
    readonly ListView scripts = new();
    readonly ListBox profiles = new();
    readonly TextBox search = new();
    readonly TextBox logBox = new();
    readonly TextBox scriptDetails = new();
    readonly Label statusLabel = new();
    readonly Label profileStatusLabel = new();
    string lastScriptUiSignature = "";
    string lastProfileUiSignature = "";
    readonly NotifyIcon tray = new();
    readonly Icon appIcon;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<int, HotkeyBinding> hotkeys = new();
    readonly string root;
    readonly string configFile;
    readonly string logDir;
    readonly string portableFlag;
    static Mutex? singleInstanceMutex;
    bool closing;
    int nextHotkeyId = 100;
    enum ProfileRunState { Idle, Starting, Stopping }
    readonly Dictionary<string, ProfileRunState> profileStates = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CancellationTokenSource> profileCancellation = new(StringComparer.OrdinalIgnoreCase);
    bool futureConfigVersion;
    string? contextScriptId;

    public MainForm()
    {
        bool created;
        singleInstanceMutex = new Mutex(true, @"Global\AHKScriptManager_v2", out created);
        if (!created) { MessageBox.Show("AHK Script Manager가 이미 실행 중입니다.", "AHK Script Manager", MessageBoxButtons.OK, MessageBoxIcon.Information); Environment.Exit(0); }
        Text = "AHK Script Manager v3.3.3"; Width = 1180; Height = 720; MinimumSize = new Size(980, 620); BackColor = Color.FromArgb(248,249,250); Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        appIcon = LoadAppIcon();
        Icon = appIcon;
        AllowDrop = true;
        DragEnter += OnDragEnter; DragDrop += OnDragDrop;
        root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        portableFlag = Path.Combine(root, "portable.flag");
        bool portable = Environment.GetEnvironmentVariable("AHK_MANAGER_PORTABLE") == "1" || File.Exists(portableFlag);
        string dataRoot = portable ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AHKScriptManager");
        Directory.CreateDirectory(dataRoot);
        configFile = Path.Combine(dataRoot, "config.json");
        logDir = Path.Combine(dataRoot, "logs"); Directory.CreateDirectory(logDir);
        LoadConfig(); BuildUi(); SetupTray(); RegisterAllHotkeys();
        if (cfg.StartMinimized)
        {
            WindowState = FormWindowState.Minimized;
            if (cfg.MinimizeToTray) Hide();
        }
        timer.Tick += (_, _) => Tick(); timer.Start();
        UpdateStartup();
        Log("관리자 시작");
    }

    void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8), WrapContents = false, BackColor = Color.FromArgb(245,246,248) };
        search.Width = 250; search.PlaceholderText = "🔍 스크립트 검색"; search.TextChanged += (_, _) => RefreshScripts(true);
        top.Controls.Add(search);
        top.Controls.Add(Btn("＋ 등록", (_,_) => AddScript()));
        top.Controls.Add(Btn("📂 폴더", (_,_) => AddFolder()));
        top.Controls.Add(Btn("▶ 전체 실행", (_,_) => RunAll()));
        top.Controls.Add(Btn("■ 전체 종료", (_,_) => StopAll()));
        top.Controls.Add(Btn("🛑 긴급 종료", (_,_) => EmergencyStop()));
        top.Controls.Add(Btn("💾 백업", (_,_) => BackupScripts()));
        top.Controls.Add(Btn("↕ 설정", (_,_) => ExportImport()));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage("스크립트"); var t2 = new TabPage("프로필"); var t3 = new TabPage("로그"); var t4 = new TabPage("설정");
        BuildScriptTab(t1); BuildProfileTab(t2); BuildLogTab(t3); BuildSettingsTab(t4);
        tabs.TabPages.AddRange(new[] { t1,t2,t3,t4 });
        statusLabel.AutoSize = true; statusLabel.Text = "준비됨"; statusLabel.Dock = DockStyle.Bottom; statusLabel.Height = 24; statusLabel.Padding = new Padding(8,4,0,0); statusLabel.BackColor = Color.FromArgb(245,246,248); statusLabel.ForeColor = Color.FromArgb(90,94,100);
        Controls.Add(statusLabel); Controls.Add(tabs); Controls.Add(top);
    }

    void BuildScriptTab(TabPage tab)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 390, FixedPanel = FixedPanel.Panel2 };
        scripts.Dock = DockStyle.Fill;
        scripts.View = View.Details; scripts.FullRowSelect = true; scripts.GridLines = false; scripts.MultiSelect = false;
        scripts.HideSelection = false; scripts.BorderStyle = BorderStyle.None; scripts.Font = new Font("Segoe UI", 9F);
        scripts.BackColor = Color.White; scripts.ForeColor = Color.FromArgb(35, 38, 42);
        scripts.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        foreach (var x in new[] { ("상태",80),("이름",180),("그룹",110),("AHK",70),("PID",70),("시작",125),("종료",125),("대상 프로세스",150),("경로",430) }) scripts.Columns.Add(x.Item1,x.Item2);
        scripts.DoubleClick += (_,_) => EditSelected();
        scripts.SelectedIndexChanged += (_,_) => UpdateScriptDetails();
        scripts.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = scripts.HitTest(e.Location);
            if (hit.Item == null) { contextScriptId = null; scripts.SelectedItems.Clear(); UpdateScriptDetails(); return; }
            hit.Item.Selected = true; hit.Item.Focused = true; contextScriptId = (hit.Item.Tag as ScriptItem)?.Id;
        };
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var runItem = menu.Items.Add("▶ 실행", null, (_,_) => RunContextSelected());
        var stopItem = menu.Items.Add("■ 종료", null, (_,_) => StopContextSelected());
        var restartItem = menu.Items.Add("↻ 재시작", null, (_,_) => RestartContextSelected());
        menu.Items.Add(new ToolStripSeparator());
        var editItem = menu.Items.Add("✎ 편집", null, (_,_) => EditContextSelected());
        var backupItem = menu.Items.Add("▣ 백업", null, (_,_) => BackupContextSelected());
        var removeItem = menu.Items.Add("× 제거", null, (_,_) => RemoveContextSelected());
        menu.Opening += (_,_) => { var x = GetContextSelected(); bool ok = x != null; runItem.Enabled = stopItem.Enabled = restartItem.Enabled = editItem.Enabled = backupItem.Enabled = removeItem.Enabled = ok; };
        scripts.ContextMenuStrip = menu;

        scriptDetails.Dock = DockStyle.Fill; scriptDetails.Multiline = true; scriptDetails.ReadOnly = true; scriptDetails.BorderStyle = BorderStyle.None;
        scriptDetails.BackColor = Color.FromArgb(248,249,250); scriptDetails.ForeColor = Color.FromArgb(55,58,62); scriptDetails.Font = new Font("Segoe UI", 9F); scriptDetails.Padding = new Padding(8);
        split.Panel1.Controls.Add(scripts); split.Panel2.Padding = new Padding(8); split.Panel2.Controls.Add(scriptDetails);
        tab.Controls.Add(split); RefreshScripts(true);
    }

    string GetScriptStateText(ScriptItem s)
    {
        RefreshPid(s);
        if(!s.Enabled) return "× 비활성";
        if(!s.Pid.HasValue) return "○ 대기";
        try
        {
            using var p=Process.GetProcessById(s.Pid.Value);
            if(p.HasExited) return "○ 대기";
            string? exe=null;
            try{exe=p.MainModule?.FileName;}catch{}
            var expected=ResolveAhk(s);
            if(!string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(expected) && !exe.Equals(expected,StringComparison.OrdinalIgnoreCase))
                return "⚠ AHK 프로세스 확인 필요";
            return "● AHK 실행 중";
        }
        catch{return "⚠ 프로세스 확인 필요";}
    }

    void UpdateScriptDetails()
    {
        var s = Selected();
        if (s == null) { scriptDetails.Text = "스크립트를 선택하면 상세 정보가 표시됩니다."; return; }
        string state = GetScriptStateText(s);
        string targetMode = s.AutomaticTargetMonitoring ? "자동 연동 ON" : "수동";
        string targetOptions = $"시작 시 실행={(s.StartWhenTargetStarts ? "ON" : "OFF")} / 종료 시 종료={(s.StopWhenTargetExits ? "ON" : "OFF")}";
        string restart = s.AutoRestart ? $"자동 재실행 ON ({s.MaxRestarts}회/{s.RestartWindowSeconds}초, {s.RestartDelayMs}ms)" : "자동 재실행 OFF";
        scriptDetails.Text = $"{s.Name}    {state}\r\n" +
            $"AHK: {s.Version}    PID: {(s.Pid?.ToString() ?? "-")}    그룹: {s.Group}\r\n" +
            $"대상 프로세스: {(string.IsNullOrWhiteSpace(s.TargetProcess) ? "없음" : s.TargetProcess)}    [{targetMode}]\r\n" +
            $"프로세스 연동: {targetOptions}\r\n" +
            $"재실행: {restart}    파일 변경 감시={(s.WatchFile ? "ON" : "OFF")}    관리자={(s.RunAsAdmin ? "ON" : "OFF")}\r\n" +
            $"경로: {s.Path}\r\n" +
            $"설명: {s.Description}";
    }

    void BuildProfileTab(TabPage tab)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300, BackColor = Color.FromArgb(245,246,248) };
        profiles.Dock = DockStyle.Fill; profiles.BorderStyle = BorderStyle.None; profiles.Font = new Font("Segoe UI", 9.5F); profiles.BackColor = Color.White; profiles.IntegralHeight = false; split.Panel1.Padding = new Padding(8); split.Panel1.BackColor = Color.FromArgb(245,246,248); split.Panel1.Controls.Add(profiles);
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = Color.White };
        var name = new TextBox { Left = 10, Top = 28, Width = 250 };
        var start = new TextBox { Left = 275, Top = 28, Width = 150 };
        var stop = new TextBox { Left = 440, Top = 28, Width = 150 };
        var startDelay = new NumericUpDown { Left = 275, Top = 78, Width = 120, Maximum = 600000, Increment = 100, Value = 500 };
        var stopDelay = new NumericUpDown { Left = 440, Top = 78, Width = 120, Maximum = 600000, Increment = 100, Value = 200 };
        var checks = new CheckedListBox { Left = 10, Top = 125, Width = 580, Height = 350, CheckOnClick = true };
        profileStatusLabel.Left = 10; profileStatusLabel.Top = 478; profileStatusLabel.Width = 580; profileStatusLabel.Height = 24; profileStatusLabel.Text = "상태: 선택된 프로필 없음"; profileStatusLabel.ForeColor = Color.FromArgb(90,94,100);
        panel.Controls.AddRange(new Control[] { new Label { Text = "프로필 이름", Left = 10, Top = 8 }, name, new Label { Text = "시작 단축키", Left = 275, Top = 8 }, start, new Label { Text = "종료 단축키", Left = 440, Top = 8 }, stop, new Label { Text = "시작 간격(ms)", Left = 275, Top = 58 }, startDelay, new Label { Text = "종료 간격(ms)", Left = 440, Top = 58 }, stopDelay, new Label { Text = "포함할 스크립트", Left = 10, Top = 105 }, checks, profileStatusLabel });
        var add = Btn("＋ 새 프로필", (_,_) => { AddProfile(); LoadProfileEditor(name,start,stop,startDelay,stopDelay,checks); }); add.Left=10; add.Top=510;
        var save = Btn("저장", (_,_) => SaveProfileEditor(name,start,stop,startDelay,stopDelay,checks)); save.Left=125; save.Top=510;
        var run = Btn("▶ 실행", (_,_) => RunProfile()); run.Left=240; run.Top=510;
        var end = Btn("■ 종료", (_,_) => StopProfile()); end.Left=355; end.Top=510;
        var del = Btn("삭제", (_,_) => DeleteProfile()); del.Left=470; del.Top=510;
        panel.Controls.AddRange(new Control[] { add,save,run,end,del });
        profiles.SelectedIndexChanged += (_,_) => LoadProfileEditor(name,start,stop,startDelay,stopDelay,checks);
        split.Panel2.Controls.Add(panel); tab.Controls.Add(split); RefreshProfiles(true);
    }

    void LoadProfileEditor(TextBox n, TextBox sk, TextBox ek, NumericUpDown sd, NumericUpDown ed, CheckedListBox checks)
    {
        var p=SelectedProfile(); checks.Items.Clear(); foreach(var s in cfg.Scripts) checks.Items.Add(s, p?.ScriptIds.Contains(s.Id)==true);
        if(p==null){n.Text=sk.Text=ek.Text="";sd.Value=500;ed.Value=200;profileStatusLabel.Text="상태: 선택된 프로필 없음";return;} n.Text=p.Name;sk.Text=p.StartHotkey;ek.Text=p.StopHotkey;sd.Value=Math.Clamp(p.StartDelayMs,0,600000);ed.Value=Math.Clamp(p.StopDelayMs,0,600000);profileStatusLabel.Text=$"상태: {GetProfileState(p.Id)}  ·  포함 스크립트 {p.ScriptIds.Count}개";
    }

    void SaveProfileEditor(TextBox n, TextBox sk, TextBox ek, NumericUpDown sd, NumericUpDown ed, CheckedListBox checks)
    {
        var p=SelectedProfile();
        if(p==null)return;
        if(profileStates.TryGetValue(p.Id,out var state)&&state!=ProfileRunState.Idle)
        {
            MessageBox.Show("실행 중인 프로필은 수정할 수 없습니다. 먼저 프로필을 종료하세요.","프로필 수정",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        p.Name=n.Text.Trim();p.StartHotkey=sk.Text.Trim();p.StopHotkey=ek.Text.Trim();p.StartDelayMs=(int)sd.Value;p.StopDelayMs=(int)ed.Value;p.ScriptIds=checks.CheckedItems.Cast<ScriptItem>().Select(x=>x.Id).ToList();SaveConfig();RegisterAllHotkeys();RefreshProfiles(true);Log($"프로필 저장: {p.Name}");
    }

    void BuildLogTab(TabPage tab)
    {
        logBox.Dock = DockStyle.Fill; logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Both; logBox.Font = new System.Drawing.Font("Consolas",9); tab.Controls.Add(logBox);
    }

    void BuildSettingsTab(TabPage tab)
    {
        var p = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, Padding=new Padding(15), ColumnCount=3 };
        var startWin = new CheckBox { Text="Windows 시작", Checked=cfg.StartWithWindows, AutoSize=true };
        var trayStart = new CheckBox { Text="시작 시 트레이", Checked=cfg.StartMinimized, AutoSize=true };
        var minTray = new CheckBox { Text="최소화 시 트레이", Checked=cfg.MinimizeToTray, AutoSize=true };
        var portable = new CheckBox { Text="포터블 모드", Checked=cfg.PortableMode, AutoSize=true };
        var stopExit = new CheckBox { Text="Manager 종료 시 실행 중인 AHK도 종료", Checked=cfg.StopScriptsOnManagerExit, AutoSize=true };
        var emergency = new TextBox { Text=cfg.EmergencyStopHotkey, Width=180 };
        var stopMs = new NumericUpDown { Minimum=250, Maximum=10000, Increment=250, Value=Math.Clamp(cfg.GracefulStopTimeoutMs,250,10000), Width=120 };
        p.Controls.Add(startWin); p.Controls.Add(trayStart); p.Controls.Add(minTray); p.Controls.Add(portable);
        p.Controls.Add(stopExit);
        p.Controls.Add(new Label{Text="긴급 전체 종료 단축키",AutoSize=true}); p.Controls.Add(emergency);
        p.Controls.Add(new Label{Text="정상 종료 대기(ms)",AutoSize=true}); p.Controls.Add(stopMs);
        var save=Btn("설정 저장",(_,_)=>{cfg.StartWithWindows=startWin.Checked;cfg.StartMinimized=trayStart.Checked;cfg.MinimizeToTray=minTray.Checked;cfg.PortableMode=portable.Checked;cfg.EmergencyStopHotkey=emergency.Text;cfg.StopScriptsOnManagerExit=stopExit.Checked;cfg.GracefulStopTimeoutMs=(int)stopMs.Value;SaveConfig();ApplyPortableMode();RegisterAllHotkeys();UpdateStartup();Log("설정 저장");});
        p.Controls.Add(save); tab.Controls.Add(p);
    }

    Button Btn(string text, EventHandler e) { var b=new Button{Text=text,Width=105,Height=30,FlatStyle=FlatStyle.System,Margin=new Padding(3)};b.Click+=e;return b; }

    void AddScript(){using var d=new OpenFileDialog{Filter="AutoHotkey (*.ahk)|*.ahk",Multiselect=true};if(d.ShowDialog()!=DialogResult.OK)return;foreach(var p in d.FileNames)Register(p);SaveConfig();RefreshScripts(true);}
    void AddFolder(){using var d=new FolderBrowserDialog();if(d.ShowDialog()!=DialogResult.OK)return;SafeRegisterFolder(d.SelectedPath);SaveConfig();RefreshScripts(true);}
    void SafeRegisterFolder(string dir){try{foreach(var p in Directory.EnumerateFiles(dir,"*.ahk",SearchOption.AllDirectories))Register(p);}catch(Exception ex){Log($"폴더 등록 일부 실패: {dir} / {ex.Message}");}}
    void Register(string path){path=Path.GetFullPath(path);if(cfg.Scripts.Any(s=>s.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))return;cfg.Scripts.Add(new ScriptItem{Name=Path.GetFileNameWithoutExtension(path),Path=path,Version=DetectVersion(path),WorkingDirectory=Path.GetDirectoryName(path)??""});Log($"등록: {path}");}
    string DetectVersion(string path){try{using var r=new StreamReader(path);char[] b=new char[32768];int n=r.ReadBlock(b,0,b.Length);var s=new string(b,0,n);if(s.Contains("#Requires AutoHotkey v2",StringComparison.OrdinalIgnoreCase))return "v2";if(s.Contains("#Requires AutoHotkey v1",StringComparison.OrdinalIgnoreCase))return "v1";}catch{}return "Auto";}
    ScriptItem? Selected(){return scripts.SelectedItems.Count==0?null:scripts.SelectedItems[0].Tag as ScriptItem;}

    void RefreshScripts(bool force = false)
    {
        var q = search.Text.Trim();
        foreach (var s in cfg.Scripts) RefreshPid(s);
        var visible = cfg.Scripts.Where(s => q == "" || s.Name.Contains(q,StringComparison.OrdinalIgnoreCase) || s.Group.Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();
        var signature = string.Join("|", visible.Select(s => $"{s.Id}:{s.Pid}:{s.Enabled}:{s.Version}:{s.StartHotkey}:{s.StopHotkey}:{s.TargetProcess}:{s.Path}"));
        if (!force && signature == lastScriptUiSignature) { UpdateScriptDetails(); return; }
        string? keepId = contextScriptId ?? (scripts.SelectedItems.Count > 0 ? (scripts.SelectedItems[0].Tag as ScriptItem)?.Id : null);
        lastScriptUiSignature = signature;
        scripts.BeginUpdate();
        try
        {
            scripts.Items.Clear();
            foreach (var s in visible)
            {
                var i = new ListViewItem(GetScriptStateText(s)) { Tag = s };
                i.SubItems.Add(s.Name); i.SubItems.Add(s.Group); i.SubItems.Add(s.Version); i.SubItems.Add(s.Pid?.ToString() ?? "-");
                i.SubItems.Add(s.StartHotkey); i.SubItems.Add(s.StopHotkey); i.SubItems.Add(s.TargetProcess); i.SubItems.Add(s.Path);
                if (s.Id.Equals(keepId,StringComparison.OrdinalIgnoreCase)) i.Selected = true;
                scripts.Items.Add(i);
            }
        }
        finally { scripts.EndUpdate(); }
        UpdateScriptDetails(); UpdateStatusLabel();
    }

    void RefreshProfiles(bool force = false)
    {
        var signature = string.Join("|", cfg.Profiles.Select(p => $"{p.Id}:{p.Name}:{p.ScriptIds.Count}:{GetProfileState(p.Id)}"));
        if (!force && signature == lastProfileUiSignature) return;
        string? keepId = SelectedProfile()?.Id; lastProfileUiSignature = signature;
        profiles.BeginUpdate();
        try { profiles.Items.Clear(); foreach (var p in cfg.Profiles) { int index = profiles.Items.Add(p); if (p.Id.Equals(keepId,StringComparison.OrdinalIgnoreCase)) profiles.SelectedIndex = index; } }
        finally { profiles.EndUpdate(); }
        UpdateStatusLabel();
    }

    void UpdateStatusLabel()
    {
        int running = cfg.Scripts.Count(x => x.Pid.HasValue);
        statusLabel.Text = $"스크립트 {cfg.Scripts.Count}개  ·  실행 중 {running}개  ·  프로필 {cfg.Profiles.Count}개";
    }
    void RefreshPid(ScriptItem s)
    {
        if (!s.Pid.HasValue) return;
        try
        {
            var p = Process.GetProcessById(s.Pid.Value);
            if (p.HasExited) { ClearRuntimeProcessState(s); return; }
            if (s.ProcessStartTimeUtc.HasValue)
            {
                DateTime actual;
                try { actual = p.StartTime.ToUniversalTime(); }
                catch { actual = s.ProcessStartTimeUtc.Value; }
                if (Math.Abs((actual - s.ProcessStartTimeUtc.Value).TotalSeconds) > 0.5)
                {
                    Log($"PID 재사용 감지: {s.Name} 기존 PID={s.Pid}");
                    ClearRuntimeProcessState(s);
                }
            }
        }
        catch { ClearRuntimeProcessState(s); }
    }

    static void ClearRuntimeProcessState(ScriptItem s)
    {
        s.Pid = null;
        s.ProcessStartTimeUtc = null;
    }

    void RunSelected(){var s=Selected();if(s!=null)RunScript(s);}
    void StopSelected(){var s=Selected();if(s!=null)StopScript(s);}
    void RunAll(){foreach(var s in cfg.Scripts.Where(x=>x.Enabled))RunScript(s);}
    void StopAll(){foreach(var s in cfg.Scripts.ToList())StopScript(s);}
    void EmergencyStop(){foreach(var s in cfg.Scripts.ToList())StopScript(s,true);Log("!!! 긴급 전체 종료 !!!");}

    void RunScript(ScriptItem s, bool recovery=false, bool manual=true, RestartReason reason=RestartReason.None)
    {
        RefreshPid(s);
        if(!s.Enabled||s.Pid.HasValue)return;
        if(!File.Exists(s.Path)){Log($"실행 실패: 스크립트 파일 없음 - {s.Name} / {s.Path}");return;}
        var exe=ResolveAhk(s);
        if(exe==""){Log($"실행 실패: AHK 실행기 결정 불가 - {s.Name} / 버전을 명시하거나 AHK 실행 파일을 지정하세요.");MessageBox.Show($"{s.Name}\nAHK v1/v2를 자동으로 결정할 수 없습니다.\n스크립트 설정에서 버전 또는 AHK 실행 파일을 지정하세요.");return;}
        try
        {
            string detectedVersion=DetectVersion(s.Path);
            Log($"AHK 실행 준비: {s.Name} / 지정={s.Version} / 감지={detectedVersion} / 실행기={exe}");
            var psi=new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Directory.Exists(s.WorkingDirectory)?s.WorkingDirectory:(Path.GetDirectoryName(s.Path)??"")};
            psi.ArgumentList.Add(s.Path);
            foreach(var arg in SplitArguments(s.Arguments)) psi.ArgumentList.Add(arg);
            if(s.RunAsAdmin)psi.Verb="runas";
            var p=Process.Start(psi);
            if(p!=null)
            {
                try
                {
                    if(p.WaitForExit(300))
                    {
                        Log($"AHK가 시작 직후 종료됨: {s.Name} PID={p.Id} ExitCode={p.ExitCode} / 스크립트 오류 가능성");
                        return;
                    }
                }
                catch(Exception waitEx){Log($"AHK 시작 상태 확인 실패: {s.Name} / {waitEx.Message}");}
                s.Pid=p.Id;
                s.DesiredRunning=true;
                s.ManualOverride=manual;
                s.RestartPending=false;
                s.RestartReason=reason;
                if(!recovery){s.RestartCount=0;s.RestartHistoryUtc.Clear();}
                s.LastStart=DateTime.Now;
                try{s.ProcessStartTimeUtc=p.StartTime.ToUniversalTime();}catch{s.ProcessStartTimeUtc=null;}
                try
                {
                    var info=new FileInfo(s.Path);
                    s.LastFileWrite=info.Exists?info.LastWriteTimeUtc:DateTime.MinValue;
                    s.LastObservedFileWrite=s.LastFileWrite;
                    s.LastObservedFileLength=info.Exists?info.Length:-1;
                    s.LastFileLength=s.LastObservedFileLength;
                    s.FileChangePendingSince=null;
                }catch{}
                Log($"실행: {s.Name} PID={s.Pid}" + (reason!=RestartReason.None ? $" / 원인={reason}" : ""));
                SaveConfig();RefreshScripts();
            }
        }
        catch(Exception ex){Log($"실행 오류: {s.Name} / {ex.Message}");}
    }

    static IEnumerable<string> SplitArguments(string text)
    {
        if(string.IsNullOrWhiteSpace(text))yield break;
        var current=new System.Text.StringBuilder();
        bool inQuotes=false;
        int backslashes=0;
        for(int i=0;i<text.Length;i++)
        {
            char c=text[i];
            if(c=='\\') { backslashes++; continue; }
            if(c=='"')
            {
                if(backslashes%2==1)
                {
                    current.Append('\\',backslashes/2);
                    current.Append('"');
                }
                else
                {
                    current.Append('\\',backslashes/2);
                    inQuotes=!inQuotes;
                }
                backslashes=0;
                continue;
            }
            if(backslashes>0){current.Append('\\',backslashes);backslashes=0;}
            if(char.IsWhiteSpace(c)&&!inQuotes)
            {
                if(current.Length>0){yield return current.ToString();current.Clear();}
            }
            else current.Append(c);
        }
        if(backslashes>0)current.Append('\\',backslashes);
        if(current.Length>0)yield return current.ToString();
    }

    void StopScript(ScriptItem s,bool force=false,bool clearProfileOwnership=true)
    {
        s.DesiredRunning=false;
        s.ManualOverride=false;
        if(clearProfileOwnership) s.ActiveProfileIds.Clear();
        s.RestartPending=false;
        s.RestartReason=RestartReason.None;
        RefreshPid(s);
        if(!s.Pid.HasValue)return;
        var pid=s.Pid.Value;
        try
        {
            var p=Process.GetProcessById(pid);
            if(force||!p.CloseMainWindow()||!p.WaitForExit(cfg.GracefulStopTimeoutMs))p.Kill(true);
            Log($"종료: {s.Name} PID={pid}");
        }
        catch(Exception ex){Log($"종료 오류: {s.Name} / {ex.Message}");}
        finally{ClearRuntimeProcessState(s);SaveConfig();RefreshScripts(true);}
    }

    string ResolveAhk(ScriptItem s)
    {
        if(File.Exists(s.AhkExe))return s.AhkExe;
        string version=s.Version;
        if(version.Equals("Auto",StringComparison.OrdinalIgnoreCase) && File.Exists(s.Path))
        {
            var detected=DetectVersion(s.Path);
            if(!detected.Equals("Auto",StringComparison.OrdinalIgnoreCase)) version=detected;
        }
        if(version.Contains("v2",StringComparison.OrdinalIgnoreCase))
            foreach(var p in new[]{cfg.AhkV2Path,@"C:\Program Files\AutoHotkey\v2\AutoHotkey.exe"})if(File.Exists(p))return p;
        if(version.Contains("v1",StringComparison.OrdinalIgnoreCase))
            foreach(var p in new[]{cfg.AhkV1Path,@"C:\Program Files\AutoHotkey\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkeyU64.exe",@"C:\Program Files (x86)\AutoHotkey\AutoHotkey.exe"})if(File.Exists(p))return p;
        var configured=new[]{cfg.AhkV1Path,cfg.AhkV2Path}.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if(configured.Count==1)return configured[0];
        var defaults=new[]{@"C:\Program Files\AutoHotkey\v2\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkeyU64.exe",@"C:\Program Files (x86)\AutoHotkey\AutoHotkey.exe"}.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return defaults.Count==1?defaults[0]:"";
    }

    void Tick()
    {
        var targetCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in cfg.Scripts.ToList())
        {
            bool wasRunning=s.Pid.HasValue;
            RefreshPid(s);
            if(wasRunning&&!s.Pid.HasValue)
            {
                Log($"프로세스 종료 감지: {s.Name}");
                PruneRestartHistory(s);
                if(s.DesiredRunning&&s.AutoRestart&s.Enabled&&s.RestartHistoryUtc.Count<s.MaxRestarts&&!s.RestartPending)
                {
                    s.RestartHistoryUtc.Add(DateTime.UtcNow);
                    s.RestartCount=s.RestartHistoryUtc.Count;
                    s.RestartReason=RestartReason.Crash;
                    s.RestartPending=true;
                    var copy=s;
                    _=Task.Run(async()=>{try{await Task.Delay(Math.Max(0,copy.RestartDelayMs));}catch{}try{BeginInvoke(()=>{copy.RestartPending=false;if(IsDisposed||!copy.DesiredRunning||!copy.Enabled||copy.Pid.HasValue)return;RunScript(copy,true,copy.ManualOverride,RestartReason.Crash);});}catch{copy.RestartPending=false;}});
                }
                else if(s.DesiredRunning&&s.AutoRestart) Log($"자동 재실행 한도 도달: {s.Name} ({s.MaxRestarts}회/{Math.Max(1,s.RestartWindowSeconds)}초)");
            }
            if(s.Pid.HasValue&&s.WatchFile&&(!s.FileRestartCooldownUntilUtc.HasValue||DateTime.UtcNow>=s.FileRestartCooldownUntilUtc.Value))
            {
                try
                {
                    var info=new FileInfo(s.Path);
                    if(!info.Exists){s.FileChangePendingSince=null;}
                    else
                    {
                        var write=info.LastWriteTimeUtc;var length=info.Length;
                        bool changedFromBaseline=write!=s.LastFileWrite||length!=s.LastFileLength;
                        if(changedFromBaseline)
                        {
                            if(write!=s.LastObservedFileWrite||length!=s.LastObservedFileLength){s.LastObservedFileWrite=write;s.LastObservedFileLength=length;s.FileChangePendingSince=DateTime.UtcNow;Log($"파일 변경 감지(안정화 대기): {s.Name}");}
                            if(s.FileChangePendingSince.HasValue&&(DateTime.UtcNow-s.FileChangePendingSince.Value).TotalMilliseconds>=500)
                            {
                                s.LastFileWrite=write;s.LastFileLength=length;s.FileChangePendingSince=null;bool wasDesired=s.DesiredRunning;bool manual=s.ManualOverride;Log($"파일 변경 확정: {s.Name}");StopScript(s,false,false);if(wasDesired){s.FileRestartCooldownUntilUtc=DateTime.UtcNow.AddSeconds(2);RunScript(s,false,manual,RestartReason.FileChanged);}
                            }
                        }
                        else s.FileChangePendingSince=null;
                    }
                }catch{}
            }
            if(s.AutomaticTargetMonitoring&&!string.IsNullOrWhiteSpace(s.TargetProcess))
            {
                string processName=Path.GetFileNameWithoutExtension(s.TargetProcess.Trim());
                if(processName.Length>0)
                {
                    if(!targetCache.TryGetValue(processName,out var target))
                    {
                        try{target=Process.GetProcessesByName(processName).Length>0;}catch{target=false;}
                        targetCache[processName]=target;
                    }
                    if(target&&s.StartWhenTargetStarts&&!s.Pid.HasValue){RunScript(s,false,false,RestartReason.TargetStarted);}
                    if(!target&&s.StopWhenTargetExits&&s.Pid.HasValue&&!s.ManualOverride)StopScript(s,false,false);
                }
            }
        }
        RefreshScripts(false);
        RefreshProfiles(false);
        UpdateStatusLabel();
    }

    void PruneRestartHistory(ScriptItem s)
    {
        int window=Math.Clamp(s.RestartWindowSeconds,1,86400);
        var cutoff=DateTime.UtcNow.AddSeconds(-window);
        s.RestartHistoryUtc.RemoveAll(x=>x<cutoff);
        s.RestartCount=s.RestartHistoryUtc.Count;
    }

    void AddProfile()
    {
        string name=Prompt("프로필 이름","새 프로필");
        if(string.IsNullOrWhiteSpace(name))return;
        var p=new ProfileItem{Name=name.Trim(),ScriptIds=cfg.Scripts.Select(s=>s.Id).ToList()};
        cfg.Profiles.Add(p);
        SaveConfig();RegisterAllHotkeys();RefreshProfiles(true);
        int index=profiles.Items.IndexOf(p);
        if(index>=0)profiles.SelectedIndex=index;
    }
    ProfileItem? SelectedProfile(){return profiles.SelectedItem as ProfileItem;}
    string GetProfileState(string id) => profileStates.TryGetValue(id,out var state) ? state switch { ProfileRunState.Starting => "시작 중", ProfileRunState.Stopping => "종료 중", _ => "대기" } : "대기";
    async void RunProfile(){var p=SelectedProfile();if(p==null)return;await RunProfileInternal(p);}
    async void StopProfile(){var p=SelectedProfile();if(p==null)return;await StopProfileInternal(p);}
    async Task RunProfileInternal(ProfileItem p)
    {
        if(!TryBeginProfile(p,ProfileRunState.Starting,out var cts))return;
        try{foreach(var id in p.ScriptIds){cts.Token.ThrowIfCancellationRequested();var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null){bool wasRunning=s.Pid.HasValue;RunScript(s,false,false,RestartReason.Profile);if(s.Pid.HasValue||wasRunning)s.ActiveProfileIds.Add(p.Id);else Log($"프로필 실행 실패/건너뜀: {p.Name} / {s.Name}");}if(p.StartDelayMs>0)await Task.Delay(p.StartDelayMs,cts.Token);}}
        catch(OperationCanceledException){Log($"프로필 시작 취소: {p.Name}");}
        finally{EndProfile(p,cts);}
    }
    async Task StopProfileInternal(ProfileItem p)
    {
        if(profileStates.TryGetValue(p.Id,out var state)&&state==ProfileRunState.Starting&&profileCancellation.TryGetValue(p.Id,out var startCts))
        {
            startCts.Cancel();
            Log($"프로필 시작 중지 요청: {p.Name}");
            foreach(var id in p.ScriptIds){var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null){s.ActiveProfileIds.Remove(p.Id);if(s.ActiveProfileIds.Count==0&&!s.ManualOverride)StopScript(s,false,false);}}
            return;
        }
        if(!TryBeginProfile(p,ProfileRunState.Stopping,out var cts))return;
        try{foreach(var id in p.ScriptIds){cts.Token.ThrowIfCancellationRequested();var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null){s.ActiveProfileIds.Remove(p.Id);if(s.ActiveProfileIds.Count==0&&!s.ManualOverride)StopScript(s,false,false);}if(p.StopDelayMs>0)await Task.Delay(p.StopDelayMs,cts.Token);}}
        catch(OperationCanceledException){Log($"프로필 종료 취소: {p.Name}");}
        finally{EndProfile(p,cts);}
    }
    bool TryBeginProfile(ProfileItem p,ProfileRunState state,out CancellationTokenSource cts)
    {
        cts=new CancellationTokenSource();
        if(profileStates.TryGetValue(p.Id,out var current)&&current!=ProfileRunState.Idle){cts.Dispose();Log($"프로필 작업 무시(진행 중): {p.Name}");return false;}
        profileStates[p.Id]=state;profileCancellation[p.Id]=cts;return true;
    }
    void EndProfile(ProfileItem p,CancellationTokenSource cts)
    {
        if(profileCancellation.TryGetValue(p.Id,out var current)&&ReferenceEquals(current,cts))profileCancellation.Remove(p.Id);
        if(cfg.Profiles.Any(x=>x.Id.Equals(p.Id,StringComparison.OrdinalIgnoreCase)))profileStates[p.Id]=ProfileRunState.Idle;
        else profileStates.Remove(p.Id);
        cts.Dispose();
        if(!IsDisposed){try{BeginInvoke(()=>RefreshProfiles(true));}catch{}}
    }
    void DeleteProfile()
    {
        var p=SelectedProfile();
        if(p==null)return;
        if(profileCancellation.TryGetValue(p.Id,out var cts))
        {
            cts.Cancel();
            Log($"프로필 삭제에 따른 작업 취소: {p.Name}");
        }
        foreach(var id in p.ScriptIds)
        {
            var s=cfg.Scripts.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
            if(s!=null){s.ActiveProfileIds.Remove(p.Id);if(s.ActiveProfileIds.Count==0&&!s.ManualOverride)StopScript(s,false,false);}
        }
        cfg.Profiles.Remove(p);
        profileStates.Remove(p.Id);
        SaveConfig();
        RegisterAllHotkeys();
        RefreshProfiles(true);
        Log($"프로필 삭제: {p.Name}");
    }

    ScriptItem? GetContextSelected()
    {
        if(!string.IsNullOrWhiteSpace(contextScriptId))
            return cfg.Scripts.FirstOrDefault(x=>x.Id.Equals(contextScriptId,StringComparison.OrdinalIgnoreCase));
        return Selected();
    }
    void RunContextSelected(){var s=GetContextSelected();if(s!=null)RunScript(s,false,true,RestartReason.None);}
    void StopContextSelected(){var s=GetContextSelected();if(s!=null)StopScript(s);}
    void RestartContextSelected(){var s=GetContextSelected();if(s==null)return;StopScript(s);RunScript(s,false,true,RestartReason.Manual);}
    void EditContextSelected(){var s=GetContextSelected();if(s==null)return;EditScript(s);}
    void BackupContextSelected(){var s=GetContextSelected();if(s!=null)Backup(s);}
    void RemoveContextSelected(){var s=GetContextSelected();if(s==null)return;contextScriptId=s.Id;RemoveScript(s);}

    void EditSelected(){var s=Selected();if(s==null)return;EditScript(s);}
    void EditScript(ScriptItem s){using var f=new ScriptEditorForm(s);if(f.ShowDialog(this)==DialogResult.OK){SaveConfig();RegisterAllHotkeys();RefreshScripts(true);Log($"스크립트 설정 저장: {s.Name}");}}
    void RemoveScript(ScriptItem s){StopScript(s);cfg.Scripts.Remove(s);foreach(var p in cfg.Profiles)p.ScriptIds.RemoveAll(id=>id.Equals(s.Id,StringComparison.OrdinalIgnoreCase));contextScriptId=null;SaveConfig();RegisterAllHotkeys();RefreshScripts(true);RefreshProfiles(true);}
    void RemoveSelected(){var s=Selected();if(s!=null)RemoveScript(s);}
    void BackupSelected(){var s=Selected();if(s!=null)Backup(s);}
    void BackupScripts(){foreach(var s in cfg.Scripts)Backup(s);Log("전체 백업 완료");}
    void Backup(ScriptItem s){try{if(!File.Exists(s.Path)){Log($"백업 실패: 파일 없음 - {s.Name}");return;}var dir=Path.Combine(Path.GetDirectoryName(configFile)!,"backup",Sanitize(s.Name));Directory.CreateDirectory(dir);var dest=Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+s.Id+".ahk");File.Copy(s.Path,dest,false);Log($"백업 완료: {s.Name}");}catch(Exception ex){Log("백업 오류: "+ex.Message);}}
    static string Sanitize(string x){foreach(var c in Path.GetInvalidFileNameChars())x=x.Replace(c,'_');return x;}

    void ExportImport(){
        var m=MessageBox.Show("예 = 내보내기 / 아니오 = 가져오기", "설정 백업", MessageBoxButtons.YesNoCancel);
        if(m==DialogResult.Cancel)return;
        if(m==DialogResult.Yes){using var d=new SaveFileDialog{Filter="JSON (*.json)|*.json",FileName="AHKScriptManager_Backup.json"};if(d.ShowDialog()==DialogResult.OK){SaveConfig();File.Copy(configFile,d.FileName,true);Log("설정 내보내기 완료");}}
        else {using var d=new OpenFileDialog{Filter="JSON (*.json)|*.json"};if(d.ShowDialog()!=DialogResult.OK)return;try{var x=JsonSerializer.Deserialize(File.ReadAllText(d.FileName), AppJsonContext.Default.AppConfig);if(x==null)throw new Exception("잘못된 설정 파일");cfg.Scripts.Clear();cfg.Profiles.Clear();cfg.Scripts.AddRange(x.Scripts ?? new List<ScriptItem>());cfg.Profiles.AddRange(x.Profiles ?? new List<ProfileItem>());cfg.StartWithWindows=x.StartWithWindows;cfg.StartMinimized=x.StartMinimized;cfg.MinimizeToTray=x.MinimizeToTray;cfg.PortableMode=x.PortableMode;cfg.EmergencyStopHotkey=x.EmergencyStopHotkey;cfg.AhkV1Path=x.AhkV1Path;cfg.AhkV2Path=x.AhkV2Path;cfg.StopScriptsOnManagerExit=x.StopScriptsOnManagerExit;cfg.GracefulStopTimeoutMs=x.GracefulStopTimeoutMs;cfg.SchemaVersion=x.SchemaVersion;futureConfigVersion=false;NormalizeConfig();SaveConfig();ApplyPortableMode();RegisterAllHotkeys();RefreshScripts(true);RefreshProfiles(true);Log("설정 가져오기 완료");}catch(Exception ex){MessageBox.Show("가져오기 실패: "+ex.Message);}}
    }

    static Icon LoadAppIcon()
    {
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AHKScriptManager.app.ico");
        if (stream != null) return new Icon(stream);
        return SystemIcons.Application;
    }

    void SetupTray(){tray.Icon=appIcon;tray.Text="AHK Script Manager";var m=new ContextMenuStrip();m.Items.Add("열기",null,(_,_)=>ShowFromTray());m.Items.Add("전체 실행",null,(_,_)=>RunAll());m.Items.Add("전체 종료",null,(_,_)=>StopAll());m.Items.Add("긴급 종료",null,(_,_)=>EmergencyStop());m.Items.Add("종료",null,(_,_)=>{closing=true;Close();});tray.ContextMenuStrip=m;tray.DoubleClick+=(s,e)=>ShowFromTray();tray.Visible=true;}
    void ShowFromTray(){Show();WindowState=FormWindowState.Normal;Activate();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(WindowState==FormWindowState.Minimized&&cfg.MinimizeToTray)Hide();}

    void RegisterAllHotkeys()
    {
        foreach(var id in hotkeys.Keys.ToList()) UnregisterHotKey(Handle,id);
        hotkeys.Clear();
        nextHotkeyId=100;
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var s in cfg.Scripts.Where(x=>x.Enabled))
        {
            TryRegister(s.StartHotkey,()=>RunScript(s),used,$"{s.Name} 시작",s);
            TryRegister(s.StopHotkey,()=>StopScript(s),used,$"{s.Name} 종료",s);
        }

        TryRegister(cfg.EmergencyStopHotkey,EmergencyStop,used,"긴급 전체 종료",null);
        foreach(var p in cfg.Profiles)
        {
            TryRegister(p.StartHotkey,()=>RunProfileById(p.Id),used,$"프로필 {p.Name} 시작",null);
            TryRegister(p.StopHotkey,()=>StopProfileById(p.Id),used,$"프로필 {p.Name} 종료",null);
        }
    }

    async void RunProfileById(string id){var p=cfg.Profiles.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));if(p==null)return;await RunProfileInternal(p);}
    async void StopProfileById(string id){var p=cfg.Profiles.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));if(p==null)return;await StopProfileInternal(p);}

    void TryRegister(string text,Action action,HashSet<string> used,string owner,ScriptItem? script)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        if(!ParseHotkey(text,out var mod,out var vk))
        {
            Log($"지원하지 않는 단축키: {text} / {owner}. 예: Ctrl+F1, Ctrl+Numpad1, Ctrl+NumpadAdd, Ctrl+[ ");
            return;
        }
        string key=$"{mod}:{vk}";
        if(!used.Add(key)){Log($"단축키 충돌: {text} / {owner}");return;}
        int id=nextHotkeyId++;
        uint registerMod=mod|MOD_NOREPEAT;
        if(RegisterHotKey(Handle,id,registerMod,vk))
        {
            hotkeys[id]=new HotkeyBinding{Action=action,Script=script,Owner=owner};
            string scope=script==null||string.IsNullOrWhiteSpace(script.TargetProcess)?"전역":"대상 프로세스 전용: "+Path.GetFileName(script.TargetProcess);
            Log($"단축키 등록: {text} / {owner} / {scope}");
        }
        else
        {
            int error=Marshal.GetLastWin32Error();
            string reason=error==ERROR_HOTKEY_ALREADY_REGISTERED?"이미 다른 프로그램이 사용 중":$"Windows 오류 {error}";
            Log($"단축키 등록 실패: {text} / {owner} / {reason}");
        }
    }
    bool ParseHotkey(string text, out uint mod, out uint vk)
    {
        mod = 0; vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int keyCount = 0;
        foreach (var raw in parts)
        {
            var p = raw.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl": case "control": mod |= MOD_CONTROL; continue;
                case "alt": mod |= MOD_ALT; continue;
                case "shift": mod |= MOD_SHIFT; continue;
                case "win": case "windows": case "lwin": case "rwin": mod |= MOD_WIN; continue;
            }

            if (keyCount++ > 0) return false;
            if (p.Length == 1 && char.IsLetterOrDigit(p[0])) { vk = char.ToUpperInvariant(p[0]); continue; }
            if (p.StartsWith("f") && int.TryParse(p[1..], out var n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); continue; }

            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["num0"] = 0x60, ["num1"] = 0x61, ["num2"] = 0x62, ["num3"] = 0x63, ["num4"] = 0x64,
                ["num5"] = 0x65, ["num6"] = 0x66, ["num7"] = 0x67, ["num8"] = 0x68, ["num9"] = 0x69,
                ["numpad0"] = 0x60, ["numpad1"] = 0x61, ["numpad2"] = 0x62, ["numpad3"] = 0x63, ["numpad4"] = 0x64,
                ["numpad5"] = 0x65, ["numpad6"] = 0x66, ["numpad7"] = 0x67, ["numpad8"] = 0x68, ["numpad9"] = 0x69,
                ["numadd"] = 0x6B, ["numpadadd"] = 0x6B, ["num+"] = 0x6B, ["numsub"] = 0x6D, ["numpadsub"] = 0x6D, ["num-"] = 0x6D,
                ["nummult"] = 0x6A, ["numpadmultiply"] = 0x6A, ["numpadmult"] = 0x6A, ["num*"] = 0x6A,
                ["numdiv"] = 0x6F, ["numpaddivide"] = 0x6F, ["num/"] = 0x6F, ["numdecimal"] = 0x6E, ["numpaddecimal"] = 0x6E,
                ["numdel"] = 0x6E, ["numpaddecimalpoint"] = 0x6E, ["numsep"] = 0x6C,
                ["pause"] = 0x13, ["space"] = 0x20, ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
                ["tab"] = 0x09, ["insert"] = 0x2D, ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23,
                ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
                ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["backspace"] = 0x08,
                ["capslock"] = 0x14, ["scrolllock"] = 0x91, ["numlock"] = 0x90, ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
                ["apps"] = 0x5D, ["menu"] = 0x5D, ["semicolon"] = 0xBA, [";"] = 0xBA, ["equals"] = 0xBB, ["="] = 0xBB,
                ["comma"] = 0xBC, [","] = 0xBC, ["minus"] = 0xBD, ["-"] = 0xBD, ["period"] = 0xBE, ["."] = 0xBE,
                ["slash"] = 0xBF, ["/"] = 0xBF, ["backquote"] = 0xC0, ["grave"] = 0xC0, ["`"] = 0xC0,
                ["lbracket"] = 0xDB, ["["] = 0xDB, ["backslash"] = 0xDC, ["\\"] = 0xDC, ["rbracket"] = 0xDD, ["]"] = 0xDD,
                ["apostrophe"] = 0xDE, ["quote"] = 0xDE, ["'"] = 0xDE
            };
            if (map.TryGetValue(p, out var v)) { vk = v; continue; }
            return false;
        }
        return keyCount == 1 && vk != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if(m.Msg==WM_HOTKEY && hotkeys.TryGetValue(m.WParam.ToInt32(),out var binding))
        {
            if(binding.Script!=null && !IsTargetProcessForeground(binding.Script.TargetProcess))
            {
                Log($"단축키 무시: {binding.Owner} / 대상 프로세스가 포그라운드가 아님");
            }
            else
            {
                try{BeginInvoke(binding.Action);}catch{}
            }
        }
        base.WndProc(ref m);
    }

    bool IsTargetProcessForeground(string targetProcess)
    {
        if(string.IsNullOrWhiteSpace(targetProcess)) return true;
        var hwnd=GetForegroundWindow();
        if(hwnd==IntPtr.Zero) return false;
        if(GetWindowThreadProcessId(hwnd,out var pid)==0 || pid==0) return false;
        try
        {
            using var p=Process.GetProcessById((int)pid);
            string actual=p.ProcessName;
            string configured=Path.GetFileNameWithoutExtension(targetProcess.Trim());
            if(configured.Contains('\\')) configured=Path.GetFileNameWithoutExtension(configured);
            return actual.Equals(configured,StringComparison.OrdinalIgnoreCase);
        }
        catch{return false;}
    }

    void UpdateStartup(){try{using var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);if(k==null)return;if(cfg.StartWithWindows)k.SetValue("AHKScriptManager",Application.ExecutablePath);else k.DeleteValue("AHKScriptManager",false);}catch(Exception ex){Log("시작프로그램 설정 실패: "+ex.Message);}}
    void LoadConfig(){
        try{
            if(!File.Exists(configFile)) return;
            string json=File.ReadAllText(configFile);
            var x=JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
            if(x==null) throw new Exception("설정 파일이 비어 있습니다.");
            if (x.Scripts != null) cfg.Scripts.AddRange(x.Scripts.Where(s => s != null));
            if (x.Profiles != null) cfg.Profiles.AddRange(x.Profiles.Where(p => p != null));
            cfg.StartWithWindows=x.StartWithWindows; cfg.StartMinimized=x.StartMinimized; cfg.MinimizeToTray=x.MinimizeToTray; cfg.PortableMode=x.PortableMode; cfg.EmergencyStopHotkey=x.EmergencyStopHotkey; cfg.AhkV1Path=x.AhkV1Path; cfg.AhkV2Path=x.AhkV2Path; cfg.StopScriptsOnManagerExit=x.StopScriptsOnManagerExit; cfg.GracefulStopTimeoutMs=x.GracefulStopTimeoutMs; cfg.SchemaVersion=x.SchemaVersion;
            NormalizeConfig();
        }catch(Exception ex){
            Log("설정 로드 실패: "+ex.Message);
            try{
                string bak=configFile+".bak";
                if(File.Exists(bak)){
                    var backup=JsonSerializer.Deserialize(File.ReadAllText(bak),AppJsonContext.Default.AppConfig);
                    if(backup!=null){
                        cfg.Scripts.Clear();cfg.Profiles.Clear();cfg.Scripts.AddRange(backup.Scripts??new List<ScriptItem>());cfg.Profiles.AddRange(backup.Profiles??new List<ProfileItem>());
                        cfg.StartWithWindows=backup.StartWithWindows;cfg.StartMinimized=backup.StartMinimized;cfg.MinimizeToTray=backup.MinimizeToTray;cfg.PortableMode=backup.PortableMode;cfg.EmergencyStopHotkey=backup.EmergencyStopHotkey;cfg.AhkV1Path=backup.AhkV1Path;cfg.AhkV2Path=backup.AhkV2Path;cfg.StopScriptsOnManagerExit=backup.StopScriptsOnManagerExit;cfg.GracefulStopTimeoutMs=backup.GracefulStopTimeoutMs;cfg.SchemaVersion=backup.SchemaVersion;
                        NormalizeConfig();SaveConfig(false);Log("백업 설정 자동 복구 완료 및 현재 설정 재생성");return;
                    }
                }
            }catch(Exception bex){Log("백업 설정 복구 실패: "+bex.Message);}
            try{string bad=configFile+".corrupt_"+DateTime.Now.ToString("yyyyMMdd_HHmmss");if(File.Exists(configFile))File.Move(configFile,bad,true);}catch{}
            cfg.Scripts.Clear();cfg.Profiles.Clear();
        }
    }
    void NormalizeConfig(){
        // Schema v1/v2 configs are backward compatible; missing SchemaVersion is treated as v1.
        if(cfg.SchemaVersion < 1) cfg.SchemaVersion = 1;
        if(cfg.SchemaVersion > ConfigSchema.Current)
        {
            futureConfigVersion = true;
            Log($"지원하지 않는 설정 스키마 버전: {cfg.SchemaVersion} (현재 {ConfigSchema.Current}). 자동 저장을 일시적으로 막습니다.");
        }
        else
        {
            futureConfigVersion = false;
            cfg.SchemaVersion = ConfigSchema.Current;
        }
        cfg.GracefulStopTimeoutMs = Math.Clamp(cfg.GracefulStopTimeoutMs, 250, 10000);
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var s in cfg.Scripts){
            if(string.IsNullOrWhiteSpace(s.Id)||!ids.Add(s.Id)) s.Id=Guid.NewGuid().ToString("N");
            s.MaxRestarts=Math.Clamp(s.MaxRestarts,0,100);
            s.RestartDelayMs=Math.Clamp(s.RestartDelayMs,0,600000);
            s.RestartWindowSeconds=Math.Clamp(s.RestartWindowSeconds,1,86400);
            s.Name=s.Name ?? "";
            s.Path=s.Path ?? "";
            s.Version=s.Version ?? "Auto";
            s.Description=s.Description ?? "";
            s.Group=s.Group ?? "Default";
            s.AhkExe=s.AhkExe ?? "";
            s.Arguments=s.Arguments ?? "";
            s.WorkingDirectory=s.WorkingDirectory ?? "";
            s.TargetProcess=s.TargetProcess ?? "";
            if(s.Version is not "Auto" and not "v1" and not "v2") s.Version="Auto";
            // Backward compatibility: existing configurations that already used target-process
            // automation keep that behavior after upgrading. New scripts default to manual mode.
            if (!s.AutomaticTargetMonitoring && (s.StartWhenTargetStarts || s.StopWhenTargetExits))
                s.AutomaticTargetMonitoring = true;
            s.StartHotkey=s.StartHotkey ?? "";
            s.StopHotkey=s.StopHotkey ?? "";
        }
        var profileIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in cfg.Profiles){
            if(string.IsNullOrWhiteSpace(p.Id)||!profileIds.Add(p.Id))p.Id=Guid.NewGuid().ToString("N");
            p.Name=p.Name ?? "";
            p.StartHotkey=p.StartHotkey ?? "";
            p.StopHotkey=p.StopHotkey ?? "";
            p.ScriptIds=(p.ScriptIds ?? new List<string>()).Where(id=>cfg.Scripts.Any(s=>s.Id==id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            p.StartDelayMs=Math.Clamp(p.StartDelayMs,0,600000);
            p.StopDelayMs=Math.Clamp(p.StopDelayMs,0,600000);
        }
    }

    void ApplyPortableMode(){
        try{
            bool want=cfg.PortableMode;
            bool current=File.Exists(portableFlag);
            if(want==current)return;
            string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AHKScriptManager");
            string localConfig=Path.Combine(local,"config.json");
            string rootConfig=Path.Combine(root,"config.json");
            if(want){
                Directory.CreateDirectory(root);
                if(File.Exists(localConfig)&&!File.Exists(rootConfig))File.Copy(localConfig,rootConfig,true);
                File.WriteAllText(portableFlag,"AHK Script Manager portable mode");
                Log("포터블 모드 활성화: 프로그램 재시작 후 적용됩니다.");
            }else{
                Directory.CreateDirectory(local);
                if(File.Exists(rootConfig))File.Copy(rootConfig,localConfig,true);
                File.Delete(portableFlag);
                Log("포터블 모드 비활성화: 프로그램 재시작 후 적용됩니다.");
            }
        }catch(Exception ex){Log("포터블 모드 전환 실패: "+ex.Message);}
    }

    void SaveConfig(bool createBackup=true){
        if(futureConfigVersion){Log("설정 저장 건너뜀: 현재 Manager보다 새로운 스키마 버전입니다.");return;}
        string tmp=configFile+".tmp";
        try{
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(tmp,JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig));
            if(createBackup&&File.Exists(configFile)){try{File.Copy(configFile,configFile+".bak",true);}catch{}}
            Exception? last=null;
            for(int i=0;i<3;i++){try{File.Move(tmp,configFile,true);last=null;break;}catch(Exception ex){last=ex;Thread.Sleep(100*(i+1));}}
            if(last!=null)throw last;
        }catch(Exception ex){Log("설정 저장 오류: "+ex.Message+" / 임시 파일: "+tmp);}
    }
    void Log(string msg){string line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";try{File.AppendAllText(Path.Combine(logDir,DateTime.Now.ToString("yyyy-MM-dd")+".log"),line+Environment.NewLine);}catch{}if(!IsDisposed){try{if(logBox.InvokeRequired)logBox.BeginInvoke(()=>logBox.AppendText(line+Environment.NewLine));else logBox.AppendText(line+Environment.NewLine);}catch{}}}
    void OnDragEnter(object? s,DragEventArgs e){e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;}
    void OnDragDrop(object? s,DragEventArgs e){if(e.Data?.GetData(DataFormats.FileDrop) is string[] a){foreach(var p in a){if(File.Exists(p)&&Path.GetExtension(p).Equals(".ahk",StringComparison.OrdinalIgnoreCase))Register(p);else if(Directory.Exists(p))SafeRegisterFolder(p);}SaveConfig();RefreshScripts(true);RegisterAllHotkeys();}}
    static string Prompt(string title,string value){using var f=new Form{Width=420,Height=140,Text=title,StartPosition=FormStartPosition.CenterParent};var t=new TextBox{Left=15,Top=15,Width=370,Text=value};var b=new Button{Text="확인",Left=300,Top=50,DialogResult=DialogResult.OK};f.Controls.Add(t);f.Controls.Add(b);f.AcceptButton=b;return f.ShowDialog()==DialogResult.OK?t.Text:"";}
    protected override void OnFormClosing(FormClosingEventArgs e){if(!closing&&cfg.MinimizeToTray&&e.CloseReason!=CloseReason.WindowsShutDown&&e.CloseReason!=CloseReason.TaskManagerClosing){e.Cancel=true;Hide();return;}closing=true;foreach(var cts in profileCancellation.Values.ToList()){try{cts.Cancel();}catch{}}foreach(var id in hotkeys.Keys.ToList())UnregisterHotKey(Handle,id);tray.Visible=false;if(cfg.StopScriptsOnManagerExit){foreach(var s in cfg.Scripts.ToList())StopScript(s,true);}SaveConfig();try{singleInstanceMutex?.ReleaseMutex();singleInstanceMutex?.Dispose();}catch{}base.OnFormClosing(e);}
}

sealed class ProcessPickerForm : Form
{
    readonly ListView list=new();
    public string SelectedProcess { get; private set; } = "";
    public ProcessPickerForm(string current)
    {
        Text="대상 프로세스 선택"; Width=560; Height=520; MinimumSize=new Size(480,400); StartPosition=FormStartPosition.CenterParent; AutoScaleMode=AutoScaleMode.Dpi;
        var filter=new TextBox{Dock=DockStyle.Top,PlaceholderText="프로세스 검색 (이름)",Margin=new Padding(8)};
        list.Dock=DockStyle.Fill;list.View=View.Details;list.FullRowSelect=true;list.MultiSelect=false;list.Columns.Add("프로세스",250);list.Columns.Add("PID",90);list.Columns.Add("경로",500);list.HideSelection=false;
        var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=42,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8),WrapContents=false};
        var ok=new Button{Text="선택",Width=90,DialogResult=DialogResult.OK};var cancel=new Button{Text="취소",Width=90,DialogResult=DialogResult.Cancel};bottom.Controls.Add(ok);bottom.Controls.Add(cancel);
        Controls.Add(list);Controls.Add(filter);Controls.Add(bottom);AcceptButton=ok;CancelButton=cancel;
        void LoadList(){string q=filter.Text.Trim();list.BeginUpdate();try{list.Items.Clear();foreach(var p in Process.GetProcesses().OrderBy(x=>x.ProcessName,StringComparer.OrdinalIgnoreCase)){try{if(q.Length>0&&!p.ProcessName.Contains(q,StringComparison.OrdinalIgnoreCase))continue;var item=new ListViewItem(p.ProcessName+".exe");item.SubItems.Add(p.Id.ToString());string path="";try{path=p.MainModule?.FileName??"";}catch{}item.SubItems.Add(path);item.Tag=p.ProcessName+".exe";list.Items.Add(item);}catch{}}}finally{list.EndUpdate();}}
        filter.TextChanged+=(a,b)=>LoadList();list.DoubleClick+=(a,b)=>{if(list.SelectedItems.Count>0){SelectedProcess=list.SelectedItems[0].Tag?.ToString()??"";DialogResult=DialogResult.OK;Close();}};
        ok.Click+=(a,b)=>{if(list.SelectedItems.Count>0)SelectedProcess=list.SelectedItems[0].Tag?.ToString()??"";else if(!string.IsNullOrWhiteSpace(current)){SelectedProcess=current.Trim();}else{DialogResult=DialogResult.Cancel;}};
        LoadList();
        if(!string.IsNullOrWhiteSpace(current)){for(int i=0;i<list.Items.Count;i++)if(string.Equals(list.Items[i].Tag?.ToString(),current.Trim(),StringComparison.OrdinalIgnoreCase)){list.Items[i].Selected=true;list.Items[i].EnsureVisible();break;}}
    }
}

sealed class ScriptEditorForm : Form
{
    readonly ScriptItem s;
    readonly TextBox name=new(), start=new(), stop=new(), exe=new(), args=new(), work=new(), group=new(), target=new();
    readonly TextBox desc=new();
    readonly ComboBox version=new();
    readonly CheckBox admin=new(), auto=new(), watch=new(), automaticTarget=new(), startTarget=new(), stopTarget=new(), enabled=new();
    readonly NumericUpDown max=new(), delay=new(), window=new();

    public ScriptEditorForm(ScriptItem item)
    {
        s=item;
        Text="스크립트 설정 - "+s.Name;
        Width=760; Height=680; MinimumSize=new Size(620,500);
        StartPosition=FormStartPosition.CenterParent; AutoScaleMode=AutoScaleMode.Dpi;
        Build();
        LoadFromModel();
    }

    void Build()
    {
        var t=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=2,AutoScroll=true};
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));

        Add(t,"이름",name);
        Add(t,"AHK 버전",version);
        version.DropDownStyle=ComboBoxStyle.DropDownList;
        version.Items.AddRange(new object[]{"Auto","v1","v2"});
        Add(t,"그룹",group);
        Add(t,"시작 단축키",start);
        Add(t,"종료 단축키",stop);

        t.Controls.Add(new Label{Text="AHK 실행 파일",AutoSize=true,Anchor=AnchorStyles.Left});
        var exePanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0),Padding=new Padding(0)};
        exePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        exePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,76));
        exe.Dock=DockStyle.Fill;
        exePanel.Controls.Add(exe,0,0);
        var pickExe=new Button{Text="찾기",Dock=DockStyle.Fill,Margin=new Padding(6,0,0,0)};
        exePanel.Controls.Add(pickExe,1,0);
        t.Controls.Add(exePanel);
        pickExe.Click+=(a,b)=>{
            using var d=new OpenFileDialog{Title="AutoHotkey 실행 파일 선택",Filter="AutoHotkey 실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",CheckFileExists=true};
            if(!string.IsNullOrWhiteSpace(exe.Text)&&File.Exists(exe.Text)) d.FileName=exe.Text;
            if(d.ShowDialog(this)==DialogResult.OK) exe.Text=d.FileName;
        };

        Add(t,"실행 인자",args);
        Add(t,"작업 디렉터리",work);

        t.Controls.Add(new Label{Text="대상 프로세스",AutoSize=true,Anchor=AnchorStyles.Left});
        var targetPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0),Padding=new Padding(0)};
        targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,111));
        target.Dock=DockStyle.Fill;
        targetPanel.Controls.Add(target,0,0);
        var pickTarget=new Button{Text="프로세스 선택",Dock=DockStyle.Fill,Margin=new Padding(6,0,0,0)};
        targetPanel.Controls.Add(pickTarget,1,0);
        t.Controls.Add(targetPanel);
        pickTarget.Click+=(a,b)=>{using var f=new ProcessPickerForm(target.Text);if(f.ShowDialog(this)==DialogResult.OK)target.Text=f.SelectedProcess;};

        Add(t,"설명",desc);
        desc.Multiline=true; desc.Height=70; desc.ScrollBars=ScrollBars.Vertical;

        AddCheck(t,admin,"관리자 권한");
        AddCheck(t,enabled,"활성화");
        AddCheck(t,auto,"비정상 종료 자동 재실행");
        AddCheck(t,watch,"파일 변경 시 자동 재시작");
        AddCheck(t,automaticTarget,"자동 프로세스 연동 사용");
        AddCheck(t,startTarget,"대상 프로세스 시작 시 실행");
        AddCheck(t,stopTarget,"대상 프로세스 종료 시 종료");

        automaticTarget.CheckedChanged+=(a,b)=>UpdateTargetOptions();

        max.Maximum=100;
        delay.Maximum=600000;
        window.Minimum=1; window.Maximum=86400;
        Add(t,"최대 재실행",max);
        Add(t,"재실행 지연(ms)",delay);
        Add(t,"재실행 제한 창(초)",window);

        var ok=new Button{Text="저장",Width=100};
        var cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel,Width=100};
        ok.Click+=(a,b)=>{
            if(!SaveToModel()) return;
            DialogResult=DialogResult.OK;
            Close();
        };
        var p=new FlowLayoutPanel{AutoSize=true};
        p.Controls.Add(ok); p.Controls.Add(cancel);
        t.Controls.Add(new Label()); t.Controls.Add(p);
        Controls.Add(t);
        AcceptButton=ok;
        CancelButton=cancel;
    }

    void LoadFromModel()
    {
        name.Text=s.Name ?? "";
        version.SelectedItem=s.Version is "v1" or "v2" ? s.Version : "Auto";
        group.Text=s.Group ?? "";
        start.Text=s.StartHotkey ?? "";
        stop.Text=s.StopHotkey ?? "";
        exe.Text=s.AhkExe ?? "";
        args.Text=s.Arguments ?? "";
        work.Text=s.WorkingDirectory ?? "";
        target.Text=s.TargetProcess ?? "";
        desc.Text=s.Description ?? "";
        admin.Checked=s.RunAsAdmin;
        enabled.Checked=s.Enabled;
        auto.Checked=s.AutoRestart;
        watch.Checked=s.WatchFile;
        automaticTarget.Checked=s.AutomaticTargetMonitoring;
        startTarget.Checked=s.StartWhenTargetStarts;
        stopTarget.Checked=s.StopWhenTargetExits;
        max.Value=Math.Clamp(s.MaxRestarts,0,100);
        delay.Value=Math.Clamp(s.RestartDelayMs,0,600000);
        window.Value=Math.Clamp(s.RestartWindowSeconds,1,86400);
        UpdateTargetOptions();
    }

    bool SaveToModel()
    {
        try
        {
            s.Name=name.Text.Trim();
            s.Version=version.SelectedItem?.ToString() ?? "Auto";
            s.Group=group.Text.Trim();
            s.StartHotkey=start.Text.Trim();
            s.StopHotkey=stop.Text.Trim();
            s.AhkExe=exe.Text.Trim();
            s.Arguments=args.Text;
            s.WorkingDirectory=work.Text.Trim();
            s.TargetProcess=target.Text.Trim();
            s.Description=desc.Text;
            s.RunAsAdmin=admin.Checked;
            s.Enabled=enabled.Checked;
            s.AutoRestart=auto.Checked;
            s.WatchFile=watch.Checked;
            s.AutomaticTargetMonitoring=automaticTarget.Checked;
            s.StartWhenTargetStarts=startTarget.Checked;
            s.StopWhenTargetExits=stopTarget.Checked;
            s.MaxRestarts=(int)max.Value;
            s.RestartDelayMs=(int)delay.Value;
            s.RestartWindowSeconds=(int)window.Value;
            return true;
        }
        catch(Exception ex)
        {
            MessageBox.Show(this,"설정 저장 중 오류가 발생했습니다.\n\n"+ex.Message,"저장 오류",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return false;
        }
    }

    void UpdateTargetOptions()
    {
        startTarget.Enabled=automaticTarget.Checked;
        stopTarget.Enabled=automaticTarget.Checked;
    }

    void Add(TableLayoutPanel t,string label,Control c)
    {
        t.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left});
        t.Controls.Add(c);
    }

    void AddCheck(TableLayoutPanel t,CheckBox c,string text)
    {
        c.Text=text; c.AutoSize=true;
        t.Controls.Add(new Label()); t.Controls.Add(c);
    }
}

static class Program
{
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new MainForm());}
}
