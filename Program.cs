using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AHKScriptManager;

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
    public bool WatchFile { get; set; }
    public bool Enabled { get; set; } = true;
    public string TargetProcess { get; set; } = "";
    public bool StartWhenTargetStarts { get; set; }
    public bool StopWhenTargetExits { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int? Pid { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastStart { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime LastFileWrite { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int RestartCount { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool DesiredRunning { get; set; }
}

public sealed class ProfileItem
{
    public string Name { get; set; } = "";
    public List<string> ScriptIds { get; set; } = new();
    public int StartDelayMs { get; set; } = 500;
    public int StopDelayMs { get; set; } = 200;
    public string StartHotkey { get; set; } = "";
    public string StopHotkey { get; set; } = "";
}

public sealed class AppConfig
{
    public List<ScriptItem> Scripts { get; set; } = new();
    public List<ProfileItem> Profiles { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool PortableMode { get; set; }
    public string EmergencyStopHotkey { get; set; } = "Ctrl+Alt+Pause";
    public string AhkV1Path { get; set; } = "";
    public string AhkV2Path { get; set; } = "";
}

public sealed class MainForm : Form
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly AppConfig cfg = new();
    readonly ListView scripts = new();
    readonly ListBox profiles = new();
    readonly TextBox search = new();
    readonly TextBox logBox = new();
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<int, Action> hotkeys = new();
    readonly string root;
    readonly string configFile;
    readonly string logDir;
    readonly string portableFlag;
    static Mutex? singleInstanceMutex;
    bool closing;
    int nextHotkeyId = 100;

    public MainForm()
    {
        bool created;
        singleInstanceMutex = new Mutex(true, @"Global\AHKScriptManager_v2", out created);
        if (!created) { MessageBox.Show("AHK Script Manager가 이미 실행 중입니다.", "AHK Script Manager", MessageBoxButtons.OK, MessageBoxIcon.Information); Environment.Exit(0); }
        Text = "AHK Script Manager v2.3"; Width = 1180; Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
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
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6) };
        search.Width = 220; search.PlaceholderText = "🔍 스크립트 검색"; search.TextChanged += (_, _) => RefreshScripts();
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
        Controls.Add(tabs); Controls.Add(top);
    }

    void BuildScriptTab(TabPage tab)
    {
        scripts.Dock = DockStyle.Fill; scripts.View = View.Details; scripts.FullRowSelect = true; scripts.GridLines = true; scripts.MultiSelect = false;
        foreach (var x in new[] { ("상태",80),("이름",180),("그룹",110),("AHK",70),("PID",70),("시작",125),("종료",125),("대상 프로세스",150),("경로",430) }) scripts.Columns.Add(x.Item1,x.Item2);
        scripts.DoubleClick += (_,_) => EditSelected();
        var menu = new ContextMenuStrip(); menu.Items.Add("실행", null, (_,_) => RunSelected()); menu.Items.Add("종료", null, (_,_) => StopSelected()); menu.Items.Add("재시작", null, (_,_) => { StopSelected(); RunSelected(); }); menu.Items.Add("편집", null, (_,_) => EditSelected()); menu.Items.Add("백업", null, (_,_) => BackupSelected()); menu.Items.Add("제거", null, (_,_) => RemoveSelected()); scripts.ContextMenuStrip = menu;
        tab.Controls.Add(scripts); RefreshScripts();
    }

    void BuildProfileTab(TabPage tab)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300 };
        profiles.Dock = DockStyle.Fill; split.Panel1.Controls.Add(profiles);
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var name = new TextBox { Left = 10, Top = 28, Width = 250 };
        var start = new TextBox { Left = 275, Top = 28, Width = 150 };
        var stop = new TextBox { Left = 440, Top = 28, Width = 150 };
        var startDelay = new NumericUpDown { Left = 275, Top = 78, Width = 120, Maximum = 600000, Increment = 100, Value = 500 };
        var stopDelay = new NumericUpDown { Left = 440, Top = 78, Width = 120, Maximum = 600000, Increment = 100, Value = 200 };
        var checks = new CheckedListBox { Left = 10, Top = 125, Width = 580, Height = 350, CheckOnClick = true };
        panel.Controls.AddRange(new Control[] { new Label { Text = "프로필 이름", Left = 10, Top = 8 }, name, new Label { Text = "시작 단축키", Left = 275, Top = 8 }, start, new Label { Text = "종료 단축키", Left = 440, Top = 8 }, stop, new Label { Text = "시작 간격(ms)", Left = 275, Top = 58 }, startDelay, new Label { Text = "종료 간격(ms)", Left = 440, Top = 58 }, stopDelay, new Label { Text = "포함할 스크립트", Left = 10, Top = 105 }, checks });
        var add = Btn("＋ 새 프로필", (_,_) => { AddProfile(); LoadProfileEditor(name,start,stop,startDelay,stopDelay,checks); }); add.Left=10; add.Top=490;
        var save = Btn("저장", (_,_) => SaveProfileEditor(name,start,stop,startDelay,stopDelay,checks)); save.Left=125; save.Top=490;
        var run = Btn("▶ 실행", (_,_) => RunProfile()); run.Left=240; run.Top=490;
        var end = Btn("■ 종료", (_,_) => StopProfile()); end.Left=355; end.Top=490;
        var del = Btn("삭제", (_,_) => DeleteProfile()); del.Left=470; del.Top=490;
        panel.Controls.AddRange(new Control[] { add,save,run,end,del });
        profiles.SelectedIndexChanged += (_,_) => LoadProfileEditor(name,start,stop,startDelay,stopDelay,checks);
        split.Panel2.Controls.Add(panel); tab.Controls.Add(split); RefreshProfiles();
    }

    void LoadProfileEditor(TextBox n, TextBox sk, TextBox ek, NumericUpDown sd, NumericUpDown ed, CheckedListBox checks)
    {
        var p=SelectedProfile(); checks.Items.Clear(); foreach(var s in cfg.Scripts) checks.Items.Add(s, p?.ScriptIds.Contains(s.Id)==true);
        if(p==null){n.Text=sk.Text=ek.Text="";return;} n.Text=p.Name;sk.Text=p.StartHotkey;ek.Text=p.StopHotkey;sd.Value=Math.Clamp(p.StartDelayMs,0,600000);ed.Value=Math.Clamp(p.StopDelayMs,0,600000);
    }

    void SaveProfileEditor(TextBox n, TextBox sk, TextBox ek, NumericUpDown sd, NumericUpDown ed, CheckedListBox checks)
    {
        var p=SelectedProfile(); if(p==null)return; p.Name=n.Text.Trim();p.StartHotkey=sk.Text.Trim();p.StopHotkey=ek.Text.Trim();p.StartDelayMs=(int)sd.Value;p.StopDelayMs=(int)ed.Value;p.ScriptIds=checks.CheckedItems.Cast<ScriptItem>().Select(x=>x.Id).ToList();SaveConfig();RegisterAllHotkeys();RefreshProfiles();Log($"프로필 저장: {p.Name}");
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
        var emergency = new TextBox { Text=cfg.EmergencyStopHotkey, Width=180 };
        p.Controls.Add(startWin); p.Controls.Add(trayStart); p.Controls.Add(minTray); p.Controls.Add(portable);
        p.Controls.Add(new Label{Text="긴급 전체 종료 단축키",AutoSize=true}); p.Controls.Add(emergency);
        var save=Btn("설정 저장",(_,_)=>{cfg.StartWithWindows=startWin.Checked;cfg.StartMinimized=trayStart.Checked;cfg.MinimizeToTray=minTray.Checked;cfg.PortableMode=portable.Checked;cfg.EmergencyStopHotkey=emergency.Text;SaveConfig();ApplyPortableMode();RegisterAllHotkeys();UpdateStartup();Log("설정 저장");});
        p.Controls.Add(save); tab.Controls.Add(p);
    }

    Button Btn(string text, EventHandler e) { var b=new Button{Text=text,Width=105,Height=29};b.Click+=e;return b; }

    void AddScript(){using var d=new OpenFileDialog{Filter="AutoHotkey (*.ahk)|*.ahk",Multiselect=true};if(d.ShowDialog()!=DialogResult.OK)return;foreach(var p in d.FileNames)Register(p);SaveConfig();RefreshScripts();}
    void AddFolder(){using var d=new FolderBrowserDialog();if(d.ShowDialog()!=DialogResult.OK)return;foreach(var p in Directory.EnumerateFiles(d.SelectedPath,"*.ahk",SearchOption.AllDirectories))Register(p);SaveConfig();RefreshScripts();}
    void Register(string path){path=Path.GetFullPath(path);if(cfg.Scripts.Any(s=>s.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))return;cfg.Scripts.Add(new ScriptItem{Name=Path.GetFileNameWithoutExtension(path),Path=path,Version=DetectVersion(path),WorkingDirectory=Path.GetDirectoryName(path)??""});Log($"등록: {path}");}
    string DetectVersion(string path){try{var s=File.ReadAllText(path);if(s.Contains("#Requires AutoHotkey v2",StringComparison.OrdinalIgnoreCase))return "v2";if(s.Contains("#Requires AutoHotkey v1",StringComparison.OrdinalIgnoreCase))return "v1";if(s.Contains("Gui,",StringComparison.OrdinalIgnoreCase)||s.Contains("Send,",StringComparison.OrdinalIgnoreCase))return "v1?";if(s.Contains("Gui(")||s.Contains("Send("))return "v2?";}catch{}return "Auto";}
    ScriptItem? Selected(){return scripts.SelectedItems.Count==0?null:scripts.SelectedItems[0].Tag as ScriptItem;}

    void RefreshScripts(){var q=search.Text.Trim();scripts.BeginUpdate();scripts.Items.Clear();foreach(var s in cfg.Scripts.Where(s=>q==""||s.Name.Contains(q,StringComparison.OrdinalIgnoreCase)||s.Group.Contains(q,StringComparison.OrdinalIgnoreCase))){RefreshPid(s);var i=new ListViewItem(s.Pid.HasValue?"● 실행":(s.Enabled?"○ 정지":"× 비활성")){Tag=s};i.SubItems.Add(s.Name);i.SubItems.Add(s.Group);i.SubItems.Add(s.Version);i.SubItems.Add(s.Pid?.ToString()??"-");i.SubItems.Add(s.StartHotkey);i.SubItems.Add(s.StopHotkey);i.SubItems.Add(s.TargetProcess);i.SubItems.Add(s.Path);scripts.Items.Add(i);}scripts.EndUpdate();}
    void RefreshProfiles(){profiles.Items.Clear();foreach(var p in cfg.Profiles)profiles.Items.Add(p.Name);}
    void RefreshPid(ScriptItem s){if(!s.Pid.HasValue)return;try{if(Process.GetProcessById(s.Pid.Value).HasExited)s.Pid=null;}catch{s.Pid=null;}}

    void RunSelected(){var s=Selected();if(s!=null)RunScript(s);}
    void StopSelected(){var s=Selected();if(s!=null)StopScript(s);}
    void RunAll(){foreach(var s in cfg.Scripts.Where(x=>x.Enabled))RunScript(s);}
    void StopAll(){foreach(var s in cfg.Scripts.ToList())StopScript(s);}
    void EmergencyStop(){foreach(var s in cfg.Scripts.ToList())StopScript(s,true);Log("!!! 긴급 전체 종료 !!!");}

    void RunScript(ScriptItem s, bool recovery=false){RefreshPid(s);if(!s.Enabled||s.Pid.HasValue)return;if(!File.Exists(s.Path)){Log($"실행 실패: 스크립트 파일 없음 - {s.Name} / {s.Path}");return;}var exe=ResolveAhk(s);if(exe==""){Log($"실행 실패: AHK 실행기를 찾을 수 없음 - {s.Name}");MessageBox.Show($"{s.Name}\nAutoHotkey 실행 파일을 찾지 못했습니다.");return;}try{var args=$"\"{s.Path}\" {s.Arguments}".Trim();var psi=new ProcessStartInfo(exe,args){UseShellExecute=true,WorkingDirectory=Directory.Exists(s.WorkingDirectory)?s.WorkingDirectory:(Path.GetDirectoryName(s.Path)??"")};if(s.RunAsAdmin)psi.Verb="runas";var p=Process.Start(psi);if(p!=null){s.Pid=p.Id;s.DesiredRunning=true;if(!recovery)s.RestartCount=0;s.LastStart=DateTime.Now;try{s.LastFileWrite=File.GetLastWriteTimeUtc(s.Path);}catch{}Log($"실행: {s.Name} PID={s.Pid}");SaveConfig();RefreshScripts();}}catch(Exception ex){Log($"실행 오류: {s.Name} / {ex.Message}");}}
    void StopScript(ScriptItem s,bool force=false){s.DesiredRunning=false;RefreshPid(s);if(!s.Pid.HasValue)return;try{var p=Process.GetProcessById(s.Pid.Value);if(force||!p.CloseMainWindow()||!p.WaitForExit(1200))p.Kill(true);Log($"종료: {s.Name} PID={s.Pid}");}catch(Exception ex){Log($"종료 오류: {s.Name} / {ex.Message}");}finally{s.Pid=null;SaveConfig();RefreshScripts();}}
    string ResolveAhk(ScriptItem s){if(File.Exists(s.AhkExe))return s.AhkExe;if(s.Version.Contains("v2")){if(File.Exists(cfg.AhkV2Path))return cfg.AhkV2Path;foreach(var p in new[]{@"C:\Program Files\AutoHotkey\v2\AutoHotkey.exe"})if(File.Exists(p))return p;}if(s.Version.Contains("v1")){if(File.Exists(cfg.AhkV1Path))return cfg.AhkV1Path;foreach(var p in new[]{@"C:\Program Files\AutoHotkey\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkeyU64.exe",@"C:\Program Files (x86)\AutoHotkey\AutoHotkey.exe"})if(File.Exists(p))return p;}foreach(var p in new[]{cfg.AhkV2Path,cfg.AhkV1Path,@"C:\Program Files\AutoHotkey\v2\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkey.exe",@"C:\Program Files\AutoHotkey\AutoHotkeyU64.exe"})if(File.Exists(p))return p;return "";}

    void Tick(){foreach(var s in cfg.Scripts.ToList()){bool was=s.Pid.HasValue;RefreshPid(s);if(was&&!s.Pid.HasValue){Log($"프로세스 종료 감지: {s.Name}");if(s.DesiredRunning&&s.AutoRestart&&s.RestartCount<s.MaxRestarts){s.RestartCount++;var copy=s;_ = Task.Run(async()=>{await Task.Delay(Math.Max(0,copy.RestartDelayMs));if(IsDisposed||!copy.DesiredRunning||!copy.Enabled)return;try{BeginInvoke(()=>RunScript(copy, true));}catch{}});}}if(s.Pid.HasValue&&s.WatchFile){try{var t=File.GetLastWriteTimeUtc(s.Path);if(t>s.LastFileWrite){s.LastFileWrite=t;Log($"파일 변경 감지: {s.Name}");bool wasDesired=s.DesiredRunning;StopScript(s);if(wasDesired)RunScript(s);}}catch{}}if(!string.IsNullOrWhiteSpace(s.TargetProcess)){bool target=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(s.TargetProcess)).Length>0;if(target&&s.StartWhenTargetStarts&&!s.Pid.HasValue)RunScript(s);if(!target&&s.StopWhenTargetExits&&s.Pid.HasValue)StopScript(s);}}RefreshScripts();}

    void AddProfile(){string name=Prompt("프로필 이름", "새 프로필");if(string.IsNullOrWhiteSpace(name))return;cfg.Profiles.Add(new ProfileItem{Name=name,ScriptIds=cfg.Scripts.Select(s=>s.Id).ToList()});SaveConfig();RefreshProfiles();}
    ProfileItem? SelectedProfile(){return profiles.SelectedIndex<0?null:cfg.Profiles.ElementAtOrDefault(profiles.SelectedIndex);}
    async void RunProfile(){var p=SelectedProfile();if(p==null)return;foreach(var id in p.ScriptIds){var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null)RunScript(s);if(p.StartDelayMs>0)await Task.Delay(p.StartDelayMs);}}
    async void StopProfile(){var p=SelectedProfile();if(p==null)return;foreach(var id in p.ScriptIds){var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null)StopScript(s);if(p.StopDelayMs>0)await Task.Delay(p.StopDelayMs);}}
    void DeleteProfile(){var p=SelectedProfile();if(p==null)return;cfg.Profiles.Remove(p);SaveConfig();RefreshProfiles();}

    void EditSelected(){var s=Selected();if(s==null)return;using var f=new ScriptEditorForm(s);if(f.ShowDialog(this)==DialogResult.OK){SaveConfig();RegisterAllHotkeys();RefreshScripts();Log($"스크립트 설정 저장: {s.Name}");}}
    void RemoveSelected(){var s=Selected();if(s==null)return;StopScript(s);cfg.Scripts.Remove(s);SaveConfig();RegisterAllHotkeys();RefreshScripts();}
    void BackupSelected(){var s=Selected();if(s!=null)Backup(s);}
    void BackupScripts(){foreach(var s in cfg.Scripts)Backup(s);Log("전체 백업 완료");}
    void Backup(ScriptItem s){try{var dir=Path.Combine(Path.GetDirectoryName(configFile)!,"backup",Sanitize(s.Name));Directory.CreateDirectory(dir);var dest=Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd_HHmmss")+".ahk");File.Copy(s.Path,dest,true);}catch(Exception ex){Log("백업 오류: "+ex.Message);}}
    static string Sanitize(string x){foreach(var c in Path.GetInvalidFileNameChars())x=x.Replace(c,'_');return x;}

    void ExportImport(){
        var m=MessageBox.Show("예 = 내보내기 / 아니오 = 가져오기", "설정 백업", MessageBoxButtons.YesNoCancel);
        if(m==DialogResult.Cancel)return;
        if(m==DialogResult.Yes){using var d=new SaveFileDialog{Filter="JSON (*.json)|*.json",FileName="AHKScriptManager_Backup.json"};if(d.ShowDialog()==DialogResult.OK){SaveConfig();File.Copy(configFile,d.FileName,true);Log("설정 내보내기 완료");}}
        else {using var d=new OpenFileDialog{Filter="JSON (*.json)|*.json"};if(d.ShowDialog()!=DialogResult.OK)return;try{var x=JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(d.FileName));if(x==null)throw new Exception("잘못된 설정 파일");cfg.Scripts.Clear();cfg.Profiles.Clear();cfg.Scripts.AddRange(x.Scripts ?? new List<ScriptItem>());cfg.Profiles.AddRange(x.Profiles ?? new List<ProfileItem>());cfg.StartWithWindows=x.StartWithWindows;cfg.StartMinimized=x.StartMinimized;cfg.MinimizeToTray=x.MinimizeToTray;cfg.PortableMode=x.PortableMode;cfg.EmergencyStopHotkey=x.EmergencyStopHotkey;cfg.AhkV1Path=x.AhkV1Path;cfg.AhkV2Path=x.AhkV2Path;NormalizeConfig();SaveConfig();ApplyPortableMode();RegisterAllHotkeys();RefreshScripts();RefreshProfiles();Log("설정 가져오기 완료");}catch(Exception ex){MessageBox.Show("가져오기 실패: "+ex.Message);}}
    }

    void SetupTray(){tray.Icon=SystemIcons.Application;tray.Text="AHK Script Manager";var m=new ContextMenuStrip();m.Items.Add("열기",null,(_,_)=>ShowFromTray());m.Items.Add("전체 실행",null,(_,_)=>RunAll());m.Items.Add("전체 종료",null,(_,_)=>StopAll());m.Items.Add("긴급 종료",null,(_,_)=>EmergencyStop());m.Items.Add("종료",null,(_,_)=>{closing=true;Close();});tray.ContextMenuStrip=m;tray.DoubleClick+=(s,e)=>ShowFromTray();tray.Visible=true;}
    void ShowFromTray(){Show();WindowState=FormWindowState.Normal;Activate();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(WindowState==FormWindowState.Minimized&&cfg.MinimizeToTray)Hide();}

    void RegisterAllHotkeys(){foreach(var id in hotkeys.Keys.ToList())UnregisterHotKey(Handle,id);hotkeys.Clear();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var s in cfg.Scripts.Where(x=>x.Enabled)){TryRegister(s.StartHotkey,()=>RunScript(s),used,$"{s.Name} 시작");TryRegister(s.StopHotkey,()=>StopScript(s),used,$"{s.Name} 종료");}TryRegister(cfg.EmergencyStopHotkey,EmergencyStop,used,"긴급 전체 종료");foreach(var p in cfg.Profiles){TryRegister(p.StartHotkey,()=>RunProfileByName(p.Name),used,$"프로필 {p.Name} 시작");TryRegister(p.StopHotkey,()=>StopProfileByName(p.Name),used,$"프로필 {p.Name} 종료");}}
    async void RunProfileByName(string n){var p=cfg.Profiles.FirstOrDefault(x=>x.Name==n);if(p==null)return;foreach(var id in p.ScriptIds){var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null)RunScript(s);if(p.StartDelayMs>0)await Task.Delay(p.StartDelayMs);}}
    async void StopProfileByName(string n){var p=cfg.Profiles.FirstOrDefault(x=>x.Name==n);if(p==null)return;foreach(var id in p.ScriptIds){var s=cfg.Scripts.FirstOrDefault(x=>x.Id==id);if(s!=null)StopScript(s);if(p.StopDelayMs>0)await Task.Delay(p.StopDelayMs);}}
    void TryRegister(string text,Action action,HashSet<string> used,string owner){if(string.IsNullOrWhiteSpace(text))return;if(!ParseHotkey(text,out var mod,out var vk)){Log($"지원하지 않는 단축키: {text} / {owner}");return;}string key=$"{mod}:{vk}";if(!used.Add(key)){Log($"단축키 충돌: {text} / {owner}");return;}int id=nextHotkeyId++;if(RegisterHotKey(Handle,id,mod,vk))hotkeys[id]=action;else Log($"단축키 등록 실패: {text} / {owner}");}
    bool ParseHotkey(string text, out uint mod, out uint vk)
    {
        mod = 0; vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int keyCount = 0;
        foreach (var raw in parts)
        {
            var p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl": case "control": mod |= MOD_CONTROL; continue;
                case "alt": mod |= MOD_ALT; continue;
                case "shift": mod |= MOD_SHIFT; continue;
                case "win": case "windows": mod |= MOD_WIN; continue;
            }
            if (keyCount++ > 0) return false;
            if (p.Length == 1 && char.IsLetterOrDigit(p[0])) { vk = char.ToUpperInvariant(p[0]); continue; }
            if (p.StartsWith("f") && int.TryParse(p[1..], out var n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); continue; }
            if (p.StartsWith("num") && int.TryParse(p[3..], out var k) && k >= 0 && k <= 9) { vk = (uint)(0x60 + k); continue; }
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["pause"] = 0x13, ["space"] = 0x20, ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
                ["tab"] = 0x09, ["insert"] = 0x2D, ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23,
                ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26,
                ["right"] = 0x27, ["down"] = 0x28, ["backspace"] = 0x08, ["capslock"] = 0x14,
                ["scrolllock"] = 0x91, ["numlock"] = 0x90, ["printscreen"] = 0x2C
            };
            if (map.TryGetValue(p, out var v)) { vk = v; continue; }
            return false;
        }
        return keyCount == 1 && vk != 0;
    }

    protected override void WndProc(ref Message m){if(m.Msg==WM_HOTKEY&&hotkeys.TryGetValue(m.WParam.ToInt32(),out var a)){try{BeginInvoke(a);}catch{}}base.WndProc(ref m);}

    void UpdateStartup(){try{using var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);if(k==null)return;if(cfg.StartWithWindows)k.SetValue("AHKScriptManager",Application.ExecutablePath);else k.DeleteValue("AHKScriptManager",false);}catch(Exception ex){Log("시작프로그램 설정 실패: "+ex.Message);}}
    void LoadConfig(){
        try{
            if(!File.Exists(configFile)) return;
            string json=File.ReadAllText(configFile);
            var x=JsonSerializer.Deserialize<AppConfig>(json);
            if(x==null) throw new Exception("설정 파일이 비어 있습니다.");
            if (x.Scripts != null) cfg.Scripts.AddRange(x.Scripts.Where(s => s != null));
            if (x.Profiles != null) cfg.Profiles.AddRange(x.Profiles.Where(p => p != null));
            cfg.StartWithWindows=x.StartWithWindows; cfg.StartMinimized=x.StartMinimized; cfg.MinimizeToTray=x.MinimizeToTray; cfg.PortableMode=x.PortableMode; cfg.EmergencyStopHotkey=x.EmergencyStopHotkey; cfg.AhkV1Path=x.AhkV1Path; cfg.AhkV2Path=x.AhkV2Path;
            NormalizeConfig();
        }catch(Exception ex){
            try{
                string bad=configFile+".corrupt_"+DateTime.Now.ToString("yyyyMMdd_HHmmss");
                if(File.Exists(configFile)) File.Move(configFile,bad,true);
            }catch{}
            Log("설정 복구: 손상된 설정 파일을 격리했습니다. "+ex.Message);
            cfg.Scripts.Clear(); cfg.Profiles.Clear();
        }
    }
    void NormalizeConfig(){
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var s in cfg.Scripts){
            if(string.IsNullOrWhiteSpace(s.Id)||!ids.Add(s.Id)) s.Id=Guid.NewGuid().ToString("N");
            s.MaxRestarts=Math.Clamp(s.MaxRestarts,0,100);
            s.RestartDelayMs=Math.Clamp(s.RestartDelayMs,0,600000);
            s.WorkingDirectory=s.WorkingDirectory ?? "";
            s.TargetProcess=s.TargetProcess ?? "";
            s.StartHotkey=s.StartHotkey ?? "";
            s.StopHotkey=s.StopHotkey ?? "";
        }
        foreach(var p in cfg.Profiles){
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

    void SaveConfig(){
        try{
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            string tmp=configFile+".tmp";
            File.WriteAllText(tmp,JsonSerializer.Serialize(cfg,new JsonSerializerOptions{WriteIndented=true}));
            if(File.Exists(configFile)){
                try{File.Copy(configFile,configFile+".bak",true);}catch{}
            }
            File.Move(tmp,configFile,true);
        }catch(Exception ex){Log("설정 저장 오류: "+ex.Message);}
    }
    void Log(string msg){string line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";try{File.AppendAllText(Path.Combine(logDir,DateTime.Now.ToString("yyyy-MM-dd")+".log"),line+Environment.NewLine);}catch{}if(!IsDisposed){try{if(logBox.InvokeRequired)logBox.BeginInvoke(()=>logBox.AppendText(line+Environment.NewLine));else logBox.AppendText(line+Environment.NewLine);}catch{}}}
    void OnDragEnter(object? s,DragEventArgs e){e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;}
    void OnDragDrop(object? s,DragEventArgs e){if(e.Data?.GetData(DataFormats.FileDrop) is string[] a){foreach(var p in a){if(File.Exists(p)&&Path.GetExtension(p).Equals(".ahk",StringComparison.OrdinalIgnoreCase))Register(p);else if(Directory.Exists(p))foreach(var f in Directory.EnumerateFiles(p,"*.ahk",SearchOption.AllDirectories))Register(f);}SaveConfig();RefreshScripts();RegisterAllHotkeys();}}
    static string Prompt(string title,string value){using var f=new Form{Width=420,Height=140,Text=title,StartPosition=FormStartPosition.CenterParent};var t=new TextBox{Left=15,Top=15,Width=370,Text=value};var b=new Button{Text="확인",Left=300,Top=50,DialogResult=DialogResult.OK};f.Controls.Add(t);f.Controls.Add(b);f.AcceptButton=b;return f.ShowDialog()==DialogResult.OK?t.Text:"";}
    protected override void OnFormClosing(FormClosingEventArgs e){if(!closing&&cfg.MinimizeToTray){e.Cancel=true;Hide();return;}foreach(var id in hotkeys.Keys.ToList())UnregisterHotKey(Handle,id);tray.Visible=false;SaveConfig();try{singleInstanceMutex?.ReleaseMutex();singleInstanceMutex?.Dispose();}catch{}base.OnFormClosing(e);}
}

sealed class ScriptEditorForm : Form
{
    readonly ScriptItem s; readonly TextBox name=new(), start=new(), stop=new(), exe=new(), args=new(), work=new(), group=new(), target=new(); readonly TextBox desc=new(); readonly CheckBox admin=new(), auto=new(), watch=new(), startTarget=new(), stopTarget=new(), enabled=new(); readonly NumericUpDown max=new(), delay=new();
    public ScriptEditorForm(ScriptItem item){s=item;Text="스크립트 설정 - "+s.Name;Width=720;Height=600;StartPosition=FormStartPosition.CenterParent;Build();}
    void Build(){var t=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=2,AutoScroll=true};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        Add(t,"이름",name,s.Name);Add(t,"그룹",group,s.Group);Add(t,"시작 단축키",start,s.StartHotkey);Add(t,"종료 단축키",stop,s.StopHotkey);Add(t,"AHK 실행 파일",exe,s.AhkExe);Add(t,"실행 인자",args,s.Arguments);Add(t,"작업 디렉터리",work,s.WorkingDirectory);Add(t,"대상 프로세스",target,s.TargetProcess);Add(t,"설명",desc,s.Description);desc.Multiline=true;desc.Height=70;
        AddCheck(t,admin,"관리자 권한",s.RunAsAdmin);AddCheck(t,enabled,"활성화",s.Enabled);AddCheck(t,auto,"비정상 종료 자동 재실행",s.AutoRestart);AddCheck(t,watch,"파일 변경 시 자동 재시작",s.WatchFile);AddCheck(t,startTarget,"대상 프로세스 시작 시 실행",s.StartWhenTargetStarts);AddCheck(t,stopTarget,"대상 프로세스 종료 시 종료",s.StopWhenTargetExits);
        max.Maximum=100;max.Value=s.MaxRestarts;delay.Maximum=600000;delay.Value=s.RestartDelayMs;Add(t,"최대 재실행",max);Add(t,"재실행 지연(ms)",delay);
        var ok=new Button{Text="저장",DialogResult=DialogResult.OK,Width=100};var cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel,Width=100};var p=new FlowLayoutPanel{AutoSize=true};p.Controls.Add(ok);p.Controls.Add(cancel);t.Controls.Add(new Label());t.Controls.Add(p);Controls.Add(t);AcceptButton=ok;CancelButton=cancel;FormClosing+=(a,b)=>{if(DialogResult!=DialogResult.OK)return;s.Name=name.Text.Trim();s.Group=group.Text.Trim();s.StartHotkey=start.Text.Trim();s.StopHotkey=stop.Text.Trim();s.AhkExe=exe.Text.Trim();s.Arguments=args.Text;s.WorkingDirectory=work.Text.Trim();s.TargetProcess=target.Text.Trim();s.Description=desc.Text;s.RunAsAdmin=admin.Checked;s.Enabled=enabled.Checked;s.AutoRestart=auto.Checked;s.WatchFile=watch.Checked;s.StartWhenTargetStarts=startTarget.Checked;s.StopWhenTargetExits=stopTarget.Checked;s.MaxRestarts=(int)max.Value;s.RestartDelayMs=(int)delay.Value;};}
    void Add(TableLayoutPanel t,string label,Control c,string val=""){t.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left});if(c is TextBox x)x.Text=val;t.Controls.Add(c);}
    void Add(TableLayoutPanel t,string label,Control c){t.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left});t.Controls.Add(c);}
    void AddCheck(TableLayoutPanel t,CheckBox c,string text,bool value){c.Text=text;c.Checked=value;c.AutoSize=true;t.Controls.Add(new Label());t.Controls.Add(c);}
}

static class Program
{
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new MainForm());}
}
