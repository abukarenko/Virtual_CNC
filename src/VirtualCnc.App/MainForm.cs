using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using VirtualCnc.Core;

namespace VirtualCnc.App;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(17, 23, 32), Surface = Color.FromArgb(27, 35, 47), TextColor = Color.FromArgb(225, 233, 242), Accent = Color.FromArgb(58, 206, 164);
    private readonly GrblController engine;
    private readonly SerialConnection serial;
    private readonly ComboBox ports = new() { Width=120, DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly Button connect = new() { Text="Подключить", Width=112, Height=30, FlatStyle=FlatStyle.Flat };
    private readonly ComboBox baud = new() { Width=100, DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly List<SensorIndicator> axisChecks = [];
    private SensorIndicator probeIndicator=null!, doorIndicator=null!, coolantIndicator=null!;
    private readonly TrackBar spindleSlider=new(), feedSlider=new(), rapidSlider=new();
    private readonly Label spindleLabel=new(), feedLabel=new(), rapidLabel=new();
    private bool syncingControls;
    private long settingsRevision;
    private readonly Label state = new() { AutoSize=true, Font=new Font("Segoe UI", 16, FontStyle.Bold) };
    private readonly Label coordinates = new() { AutoSize=true, Font=new Font("Consolas", 11) };
    private readonly Label metrics = new() { AutoSize=true };
    private readonly Label parser = new() { AutoSize=true, Font=new Font("Consolas", 9) };
    private readonly RichTextBox log = new() { Dock=DockStyle.Fill, ReadOnly=true, Font=new Font("Consolas", 10), BorderStyle=BorderStyle.None };
    private readonly TextBox command = new() { Dock=DockStyle.Fill, Font=new Font("Consolas", 12) };
    private readonly SurfaceEditor surfaceEditor;
    private readonly ZView zView=new() { Dock=DockStyle.Fill };
    private readonly NumericUpDown material=new() {DecimalPlaces=3,Increment=1,Minimum=0,Dock=DockStyle.Fill};
    private readonly CheckBox materialProbe=new() {Text="Щуп по материалу",AutoSize=true,Checked=true};
    private readonly PathView path = new() { Dock=DockStyle.Fill };
    private readonly DataGridView settings = new() { Dock=DockStyle.Fill, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval=10 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan previous;
    private int frame;
    private volatile bool zeroZPending,zeroZContact;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, Color Color)> journal = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<MotionSegment> points = new();
    private readonly HashSet<char> manualLimits = [];
    private readonly NotifyIcon trayIcon = new();
    private readonly Icon appIcon;
    private readonly ContextMenuStrip trayMenu = new();
    private FormWindowState restoredState = FormWindowState.Normal;

    public MainForm(GrblController engine, string? initialPort = null)
    {
        this.engine = engine;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("VirtualCnc.App.Icon.ico")!) appIcon = new Icon(iconStream);
        Icon = appIcon;
        serial = new(engine);
        Text = "Virtual CNC — GRBL 1.1f"; MinimumSize = new Size(1000, 720); Size = new Size(1250, 850);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Background; ForeColor = TextColor; Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(12), ColumnCount=1, RowCount=5 };
        root.RowStyles.Add(new(SizeType.Absolute, 40)); root.RowStyles.Add(new(SizeType.Absolute, 100));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 38)); root.RowStyles.Add(new(SizeType.Absolute, 26));
        Controls.Add(root);
        var header = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, Margin=Padding.Empty };
        header.Controls.Add(new Label { Text="VIRTUAL CNC", Font=new Font("Segoe UI", 15, FontStyle.Bold), AutoSize=false, Width=180, Height=30, TextAlign=ContentAlignment.MiddleLeft, Margin=new Padding(0,0,12,0) });
        ports.Margin=connect.Margin=baud.Margin=new Padding(0,2,8,0);
        baud.Items.AddRange(new object[]{115200,250000,50000}); baud.SelectedIndex=0;
        ports.DropDown += (_,_) => RefreshPorts();
        header.Controls.Add(ports); header.Controls.Add(connect); header.Controls.Add(baud);
        var hardReset=Button("Hard reset", () => { engine.HardReset(); path.Clear(); }); hardReset.Width=112; hardReset.Margin=new Padding(0,2,0,0);
        header.Controls.Add(hardReset); root.Controls.Add(header,0,0);
        var dashboard = new TableLayoutPanel { Dock=DockStyle.Fill, BackColor=Surface, Padding=new Padding(8), ColumnCount=2, RowCount=3 };
        dashboard.ColumnStyles.Add(new(SizeType.Percent, 42)); dashboard.ColumnStyles.Add(new(SizeType.Percent, 58));
        dashboard.Controls.Add(state, 0, 0); dashboard.Controls.Add(coordinates, 1, 0); dashboard.SetRowSpan(coordinates, 2);
        dashboard.Controls.Add(metrics, 0, 1); dashboard.Controls.Add(parser, 0, 2); dashboard.SetColumnSpan(parser, 2);
        root.Controls.Add(dashboard, 0, 1);
        var tabs = new TabControl { Dock=DockStyle.Fill };
        var machineTab = new TabPage("Станок и траектория") { BackColor=Background, ForeColor=TextColor };
        var machineLayout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=3, RowCount=1, Margin=Padding.Empty };
        machineLayout.ColumnStyles.Add(new(SizeType.Percent,100)); machineLayout.ColumnStyles.Add(new(SizeType.Absolute,165)); machineLayout.ColumnStyles.Add(new(SizeType.Absolute,220));
        machineLayout.Controls.Add(path,0,0);
        var zPanel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty,Padding=new Padding(3,0,3,0)};
        zPanel.RowStyles.Add(new(SizeType.Percent,100));zPanel.RowStyles.Add(new(SizeType.Absolute,22));zPanel.RowStyles.Add(new(SizeType.Absolute,28));zPanel.RowStyles.Add(new(SizeType.Absolute,28));
        zPanel.RowStyles.Add(new(SizeType.Absolute,30));
        zPanel.Controls.Add(zView,0,0);zPanel.Controls.Add(new Label {Text="Толщина H, мм",Dock=DockStyle.Fill},0,1);zPanel.Controls.Add(material,0,2);zPanel.Controls.Add(materialProbe,0,3);
        material.Maximum=(decimal)engine.Memory.Settings[132];material.Value=(decimal)Math.Clamp(engine.Memory.MaterialThickness,0,(double)material.Maximum);materialProbe.Checked=engine.Memory.MaterialProbeEnabled;
        void UpdateMaterial()=>engine.ConfigureWorkpiece((double)material.Value,materialProbe.Checked);
        material.ValueChanged+=(_,_)=>UpdateMaterial();materialProbe.CheckedChanged+=(_,_)=>UpdateMaterial();UpdateMaterial();
        var zeroZ=Button("Ноль Z",()=>{
            if(zeroZPending)return;
            var s=engine.Snapshot();
            if(s.State!="Idle" || !materialProbe.Checked){MessageBox.Show(this,"Завершите Home, остановите движение и включите щуп по материалу.","Ноль Z");return;}
            zeroZPending=true;zeroZContact=false;
            var target=-s.MaxTravel.Z-(s.MachinePosition.Z-s.WorkPosition.Z);
            Send($"G21 G90 G38.2 Z{target.ToString("F3",CultureInfo.InvariantCulture)} F100");
        });zeroZ.Dock=DockStyle.Fill;zPanel.Controls.Add(zeroZ,0,4);
        machineLayout.Controls.Add(zPanel,1,0);
        var controls=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(8,4,8,4), AutoScroll=true, BackColor=Surface };
        void Heading(string title) => controls.Controls.Add(new Label { Text=title, Width=172, Height=25, TextAlign=ContentAlignment.MiddleCenter, Font=new Font(Font,FontStyle.Bold), Margin=Padding.Empty });
        void ActionButton(string title,Action action) { var button=Button(title,action); button.Width=172; controls.Controls.Add(button); }
        Heading("Управление");
        ActionButton("Pause / resume",()=>engine.Receive(engine.Snapshot().State.StartsWith("Hold",StringComparison.Ordinal) ? "~" : "!"));
        ActionButton("Stop",()=>engine.Receive(new byte[]{0x18}));
        ActionButton("Unlock ($X)",()=>Send("$X"));
        ActionButton("Reset (Ctrl+X)",()=>engine.Receive(new byte[]{0x18}));
        var extra=new FlowLayoutPanel { Width=178, Height=32, WrapContents=false, Margin=Padding.Empty };
        foreach(var item in new (string Text,Action Action)[]{("Home",()=>Send("$H")),("Вписать",path.FitMachine),("Очистить",path.Clear)}) {var button=Button(item.Text,item.Action);button.Width=54;button.Font=new Font(Font.FontFamily,8);extra.Controls.Add(button);}
        controls.Controls.Add(extra); Heading("Датчики");
        var sensors=new TableLayoutPanel { Width=178, Height=110, ColumnCount=3, RowCount=2, Margin=Padding.Empty };
        for(int i=0;i<3;i++) sensors.ColumnStyles.Add(new(SizeType.Percent,100f/3));
        for(int i=0;i<2;i++) sensors.RowStyles.Add(new(SizeType.Percent,50));
        probeIndicator=new("Probe",Color.Gold); doorIndicator=new("Door",Color.MediumOrchid); coolantIndicator=new("Cool",Color.DeepSkyBlue);
        probeIndicator.Changed+=()=>{if(!syncingControls) engine.SetProbe(probeIndicator.Active);};
        doorIndicator.Changed+=()=>{if(!syncingControls) engine.SetDoor(doorIndicator.Active);};
        coolantIndicator.Changed+=()=>{if(!syncingControls) engine.Receive(new byte[]{0xa0});};
        sensors.Controls.Add(probeIndicator,0,0); sensors.Controls.Add(doorIndicator,1,0); sensors.Controls.Add(coolantIndicator,2,0);
        var colors=new[]{Color.Red,Color.LimeGreen,Color.DodgerBlue};
        for(int i=0;i<3;i++)
        {
            var axis="XYZ"[i]; var pin=new SensorIndicator(axis.ToString(),colors[i],momentary:true) { Tag=axis }; axisChecks.Add(pin);
            pin.PressChanged+=pressed=>{ if(pressed) manualLimits.Add(axis); else manualLimits.Remove(axis); engine.SetLimits(string.Concat(manualLimits)); pin.Active=engine.Snapshot().Limits.Contains(axis); };
            sensors.Controls.Add(pin,i,1);
        }
        controls.Controls.Add(sensors);
        void Slider(TrackBar slider,Label label,int min,int max,int value,Action changed)
        {
            label.Width=172; label.Height=20; label.Margin=Padding.Empty; controls.Controls.Add(label);
            slider.Minimum=min;slider.Maximum=max;slider.Value=value;slider.Width=172;slider.AutoSize=false;slider.Height=26;slider.TickStyle=TickStyle.None;slider.Margin=Padding.Empty;
            slider.Scroll+=(_,_)=>{if(!syncingControls) changed();}; controls.Controls.Add(slider);
        }
        Slider(spindleSlider,spindleLabel,10,200,100,()=>SetOverride(spindleSlider.Value,engine.Snapshot().SpindleOverride,0x9c,0x9d));
        Slider(feedSlider,feedLabel,10,200,100,()=>SetOverride(feedSlider.Value,engine.Snapshot().FeedOverride,0x93,0x94));
        Slider(rapidSlider,rapidLabel,0,2,2,()=>engine.Receive(new byte[]{rapidSlider.Value switch {0=>0x97,1=>0x96,_=>0x95}}));
        var advanced=new Panel { Width=178, Height=54, Visible=false, Margin=Padding.Empty };
        advanced.Controls.Add(new Label { Text="Скорость симуляции",AutoSize=true,Top=0 });
        var speed=new NumericUpDown { Minimum=.1m,Maximum=1000,DecimalPlaces=1,Increment=.5m,Value=(decimal)Math.Clamp(engine.TimeScale,.1,1000),Width=172,Top=24 };
        speed.ValueChanged+=(_,_)=>engine.TimeScale=(double)speed.Value;advanced.Controls.Add(speed);
        ActionButton("Дополнительно ▾",()=>advanced.Visible=!advanced.Visible);controls.Controls.Add(advanced);
        machineLayout.Controls.Add(controls,2,0); machineTab.Controls.Add(machineLayout);tabs.TabPages.Add(machineTab);
        var journalTab = new TabPage("Обмен GRBL"); journalTab.Controls.Add(log); tabs.TabPages.Add(journalTab);
        var settingsTab = new TabPage("EEPROM / настройки");
        settings.Columns.Add("Setting", "Настройка"); settings.Columns.Add("Value", "Значение"); settings.Columns.Add("Description","Пояснение"); settings.Columns[0].ReadOnly=settings.Columns[2].ReadOnly=true;
        settings.Columns[0].FillWeight=15;settings.Columns[1].FillWeight=15;settings.Columns[2].FillWeight=70;
        settings.DefaultCellStyle.WrapMode=DataGridViewTriState.True;settings.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
        foreach (var pair in engine.Memory.Settings) settings.Rows.Add("$" + pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture),SettingDescriptions.Get(pair.Key));
        settings.CellEndEdit += (_, e) =>
        {
            var row = settings.Rows[e.RowIndex]; Send($"{row.Cells[0].Value}={row.Cells[1].Value}");
            row.Cells[1].Value = engine.Memory.Settings[int.Parse(row.Cells[0].Value!.ToString()![1..], CultureInfo.InvariantCulture)].ToString(CultureInfo.InvariantCulture);
        };
        settingsTab.Controls.Add(settings); tabs.TabPages.Add(settingsTab);
        surfaceEditor=new(engine);var surfaceTab=new TabPage("Неровности / Heightmap");surfaceTab.Controls.Add(surfaceEditor);tabs.TabPages.Add(surfaceTab);root.Controls.Add(tabs, 0, 2);
        var entry = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=3, Padding=new Padding(0, 3, 0, 0), Margin=Padding.Empty };
        entry.ColumnStyles.Add(new(SizeType.Percent, 100)); entry.ColumnStyles.Add(new(SizeType.Absolute, 110)); entry.ColumnStyles.Add(new(SizeType.Absolute, 130));
        entry.Controls.Add(command); entry.Controls.Add(Button("Отправить", Submit)); entry.Controls.Add(Button("Статус (?)", () => engine.Receive("?")));
        root.Controls.Add(entry, 0, 3);
        root.Controls.Add(new Label { Text="COM-пара: этот эмулятор → один порт; CNC-программа → второй порт. Локальные команды работают без COM.", Dock=DockStyle.Fill, AutoSize=true }, 0, 4);
        connect.Click += (_, _) => Connect();
        command.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(); } };
        engine.Output += OnOutput; engine.MotionAdvanced += OnMotion;
        engine.LimitsChanged += OnLimits;
        serial.Received += text => journal.Enqueue(("RX  " + text, Color.LightSkyBlue));
        serial.Fault += text => journal.Enqueue(("COM  " + text, Color.Salmon));
        timer.Tick += (_, _) => UpdateMachine();
        ApplyColors(root); RefreshPorts();
        if (initialPort is not null) Shown += (_, _) => { ports.SelectedItem=initialPort; Connect(); };
        engine.Boot(); previous = clock.Elapsed; timer.Start();
        trayMenu.Items.Add("Открыть Virtual CNC", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Выход", null, (_, _) => Close());
        trayIcon.Icon = appIcon;
        trayIcon.Text = "Virtual CNC — GRBL 1.1f";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.Visible = true;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                trayIcon.Visible = true;
                ShowInTaskbar = false;
                Hide();
            }
            else restoredState = WindowState;
        };
        FormClosed += (_, _) =>
        {
            trayIcon.Visible = false; trayIcon.Dispose(); trayMenu.Dispose();
            appIcon.Dispose();
            timer.Stop(); timer.Dispose(); engine.Output -= OnOutput; engine.MotionAdvanced -= OnMotion; engine.LimitsChanged -= OnLimits; serial.Dispose();
        };
    }
    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        WindowState = restoredState;
        Show(); Activate();
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text=text, AutoSize=false, Width=110, Height=28, FlatStyle=FlatStyle.Flat, Padding=Padding.Empty, Margin=new Padding(2) };
        button.Click += (_, _) => action(); return button;
    }
    private static void ApplyColors(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            child.ForeColor=TextColor;
            if (child is TextBoxBase or System.Windows.Forms.Button or ComboBox or TabPage) child.BackColor=Surface;
            if (child is DataGridView grid)
            {
                grid.BackgroundColor=Surface; grid.DefaultCellStyle.BackColor=Surface; grid.DefaultCellStyle.ForeColor=TextColor;
                grid.ColumnHeadersDefaultCellStyle.BackColor=Background; grid.ColumnHeadersDefaultCellStyle.ForeColor=TextColor; grid.EnableHeadersVisualStyles=false;
            }
            ApplyColors(child);
        }
    }
    private void RefreshPorts()
    {
        var selected = ports.Text; ports.Items.Clear(); ports.Items.AddRange(SerialPort.GetPortNames().Order().Cast<object>().ToArray());
        if (ports.Items.Contains(selected)) ports.SelectedItem=selected; else if (ports.Items.Count > 0) ports.SelectedIndex=0;
    }
    private void Connect()
    {
        try
        {
            if (serial.IsOpen) { serial.Close(); connect.Text="Подключить"; ports.Enabled=baud.Enabled=true; return; }
            if (ports.SelectedItem is null) { MessageBox.Show(this, "Создайте виртуальную COM-пару и откройте список портов.", "COM"); return; }
            serial.Open(ports.Text,(int)baud.SelectedItem!); connect.Text="Отключить"; ports.Enabled=baud.Enabled=false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void SetOverride(int target,int current,byte up,byte down)
    {
        engine.Receive(Enumerable.Repeat(target>current ? up : down,Math.Abs(target-current)).ToArray());
    }
    private void Submit() { var text=command.Text; command.Clear(); Send(text); }
    private void Send(string text) { journal.Enqueue(("RX  " + text, Color.LightSkyBlue)); engine.Receive(text + "\n"); }
    private void OnOutput(string text)
    {
        if(zeroZPending){if(text.StartsWith("[PRB:",StringComparison.Ordinal))zeroZContact=text.EndsWith(":1]",StringComparison.Ordinal);if(text.StartsWith("error",StringComparison.Ordinal)||text.StartsWith("ALARM",StringComparison.Ordinal)||text==GrblController.Banner)zeroZPending=zeroZContact=false;}
        journal.Enqueue(("TX  " + text,text.StartsWith("error",StringComparison.Ordinal)||text.StartsWith("ALARM",StringComparison.Ordinal)?Color.Salmon:Accent));
    }
    private void OnMotion(MotionSegment segment)
    {
        var snapshot=engine.Snapshot();var offset=(snapshot.PhysicalPosition ?? snapshot.MachinePosition)-snapshot.MachinePosition;
        points.Enqueue(segment with {Start=segment.Start+offset,End=segment.End+offset});
    }
    private void OnLimits(string axes) => journal.Enqueue(($"Концевики: {(axes.Length==0 ? "свободны" : axes)}",Color.FromArgb(255,188,81)));
    internal void RefreshPreview() { frame=4; UpdateMachine(); }
    private void UpdateMachine()
    {
        var now=clock.Elapsed; engine.Tick(now-previous); previous=now;
        while (points.TryDequeue(out var p)) path.Add(p);
        if (++frame % 5 != 0) return;
        var snapshot=engine.Snapshot();
        if(zeroZPending && zeroZContact && snapshot.State=="Idle"){zeroZPending=zeroZContact=false;Send("G92 Z0");snapshot=engine.Snapshot();}
        state.Text=snapshot.State; state.ForeColor=snapshot.State == "Alarm" ? Color.Salmon : Accent;
        foreach(var pin in axisChecks) pin.Active=snapshot.Limits.Contains((char)pin.Tag!);
        syncingControls=true;
        try {
            probeIndicator.Active=snapshot.Probe;doorIndicator.Active=snapshot.Door;coolantIndicator.Active=snapshot.Coolant!=0;
            spindleSlider.Value=snapshot.SpindleOverride;feedSlider.Value=snapshot.FeedOverride;rapidSlider.Value=snapshot.RapidOverride==100 ? 2 : snapshot.RapidOverride==50 ? 1 : 0;
        } finally { syncingControls=false; }
        spindleLabel.Text=$"Spindle   {snapshot.Spindle:F0} · {snapshot.SpindleOverride}%";feedLabel.Text=$"Feed rate   {snapshot.FeedOverride}%";rapidLabel.Text=$"Rapid speed   {snapshot.RapidOverride}%";
        var settingValues=engine.SettingsSnapshot(); var values=settingValues.ToDictionary(p=>p.Key,p=>p.Value);
        long fingerprint=17;foreach(var setting in settingValues) fingerprint=unchecked(fingerprint*31+setting.Value.GetHashCode());
        if(settingsRevision!=fingerprint && !settings.IsCurrentCellInEditMode) {foreach(DataGridViewRow row in settings.Rows) row.Cells[1].Value=values[int.Parse(row.Cells[0].Value!.ToString()![1..],CultureInfo.InvariantCulture)].ToString(CultureInfo.InvariantCulture);settingsRevision=fingerprint;}
        coordinates.Text=$"MPos  {snapshot.MachinePosition.Format()}\nWPos  {snapshot.WorkPosition.Format()}\nПлан  {snapshot.PlannedPosition.Format()}";
        metrics.Text=$"F {snapshot.Feed:F0} мм/мин  ·  S {snapshot.Spindle:F0} об/мин\nОчередь {snapshot.Buffered}/15  ·  Ov {snapshot.FeedOverride}/{snapshot.RapidOverride}/{snapshot.SpindleOverride}%";
        parser.Text=snapshot.Parser;
        int count=0;
        while (count++ < 300 && journal.TryDequeue(out var item))
        {
            log.SelectionStart=log.TextLength; log.SelectionColor=item.Color; log.AppendText(item.Text + Environment.NewLine);
        }
        if (log.TextLength > 180000) { log.Select(0,90000); log.SelectedText=""; }
        log.SelectionStart=log.TextLength; log.ScrollToCaret(); path.UpdateScene(snapshot);
        if(material.Maximum!=(decimal)snapshot.MaxTravel.Z) {material.Maximum=(decimal)snapshot.MaxTravel.Z;if(material.Value>material.Maximum)material.Value=material.Maximum;}
        zView.UpdateScene(snapshot);surfaceEditor.UpdateScene(snapshot);
    }
}
