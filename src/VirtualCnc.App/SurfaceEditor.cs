using System.Globalization;
using VirtualCnc.Core;
namespace VirtualCnc.App;

internal sealed class SurfaceEditor : UserControl
{
    private readonly GrblController engine;
    private SurfaceProfile profile;
    private Vec travel, wco, physicalOffset;
    private readonly ComboBox coordinates=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=190};
    private Vec Origin => profile.Coordinates switch {SurfaceCoordinates.Work=>wco+physicalOffset,SurfaceCoordinates.Machine=>physicalOffset,_=>new(-travel.X,-travel.Y,0)};
    private Vec Minimum => new Vec(-travel.X,-travel.Y,0)-Origin;
    private readonly Dictionary<string,NumericUpDown> inputs=[];
    private readonly CheckBox enabled=new(){Text="Имитировать неровности",AutoSize=true};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersWidth=85};
    private readonly SurfaceCanvas canvas;
    private readonly Label hint=new(){Dock=DockStyle.Fill,AutoSize=false};
    private bool syncing;
    private int seed=1;
    public SurfaceEditor(GrblController engine)
    {
        this.engine=engine;profile=engine.SurfaceSnapshot();var snapshot=engine.Snapshot();travel=snapshot.MaxTravel;wco=snapshot.MachinePosition-snapshot.WorkPosition;physicalOffset=(snapshot.PhysicalPosition ?? snapshot.MachinePosition)-snapshot.MachinePosition;
        Dock=DockStyle.Fill;
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(8)};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.Absolute,100));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,40));Controls.Add(layout);
        var toolbar=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true,AutoScroll=true};layout.Controls.Add(toolbar,0,0);
        foreach(var key in new[]{"X","Y","W","H","Δ ±","Nx","Ny"})
        {
            var box=new FlowLayoutPanel {Width=95,Height=52,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(2)};
            box.Controls.Add(new Label {Text=key is "Nx" or "Ny" ? key+" узлов" : key+" мм",AutoSize=true});
            var input=new NumericUpDown {Width=91,Minimum=key is "Nx" or "Ny" ? 2 : 0,Maximum=key is "Nx" or "Ny" ? 21 : key=="Δ ±" ? 10 : 100000,DecimalPlaces=key is "Nx" or "Ny" ? 0 : 3,Increment=key=="Δ ±" ? .1m : 1};
            inputs.Add(key,input);box.Controls.Add(input);toolbar.Controls.Add(box);input.ValueChanged+=(_,_)=>Changed();
        }
        coordinates.Items.AddRange(new object[]{"Стол: от нижнего угла","Машинные координаты","Рабочие координаты"});coordinates.SelectedIndex=(int)profile.Coordinates;toolbar.Controls.Add(coordinates);
        coordinates.SelectedIndexChanged+=(_,_)=>{if(syncing)return;var oldOrigin=Origin;profile.Width=profile.Width>0 ? profile.Width : Math.Max(.001,Minimum.X+travel.X-profile.X);profile.Height=profile.Height>0 ? profile.Height : Math.Max(.001,Minimum.Y+travel.Y-profile.Y);profile.Coordinates=(SurfaceCoordinates)coordinates.SelectedIndex;var shift=oldOrigin-Origin;profile.X+=shift.X;profile.Y+=shift.Y;LoadInputs();Save();};
        enabled.Checked=profile.Enabled;enabled.CheckedChanged+=(_,_)=>Changed();toolbar.Controls.Add(enabled);
        var generate=new Button {Text="Создать рельеф",Width=135,Height=28};generate.Click+=(_,_)=>{profile.Generate(seed++);profile.Enabled=true;syncing=true;enabled.Checked=true;syncing=false;Save();};toolbar.Controls.Add(generate);
        var flatten=new Button {Text="Ровная поверхность",Width=165,Height=28};flatten.Click+=(_,_)=>{Array.Clear(profile.Values);Save();};toolbar.Controls.Add(flatten);
        var area=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};area.ColumnStyles.Add(new(SizeType.Percent,55));area.ColumnStyles.Add(new(SizeType.Percent,45));layout.Controls.Add(area,0,1);
        canvas=new(()=>profile,()=>travel,()=>Origin);area.Controls.Add(canvas,0,0);area.Controls.Add(grid,1,0);layout.Controls.Add(hint,0,2);
        grid.CellValidating+=(_,e)=>{if(syncing)return;if(!double.TryParse(e.FormattedValue?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var delta)||!double.IsFinite(delta)||Math.Abs(delta)>profile.Amplitude){e.Cancel=true;grid.Rows[e.RowIndex].ErrorText=$"Δ от {-profile.Amplitude:0.###} до {profile.Amplitude:0.###} мм";}};
        grid.CellEndEdit+=(_,e)=>{if(syncing)return;profile.Values[e.RowIndex*profile.Columns+e.ColumnIndex]=double.Parse(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value!.ToString()!,CultureInfo.InvariantCulture);grid.Rows[e.RowIndex].ErrorText="";Save(false);};
        grid.SelectionChanged+=(_,_)=>{canvas.Selected=grid.CurrentCell is {} cell ? (cell.ColumnIndex,cell.RowIndex) : (-1,-1);canvas.Invalidate();};
        LoadInputs();RefreshGrid();
    }
    public void UpdateScene(MachineSnapshot snapshot)
    {
        var changed=travel!=snapshot.MaxTravel;travel=snapshot.MaxTravel;
        var nextWco=snapshot.MachinePosition-snapshot.WorkPosition;
        var nextOffset=(snapshot.PhysicalPosition ?? snapshot.MachinePosition)-snapshot.MachinePosition;
        var moved=wco!=nextWco || physicalOffset!=nextOffset;wco=nextWco;physicalOffset=nextOffset;
        if(changed){profile.Width=Math.Min(profile.Width,travel.X);profile.Height=Math.Min(profile.Height,travel.Y);LoadInputs();Save();}
        else if(moved){LoadInputs();canvas.Invalidate();}
    }
    private void LoadInputs()
    {
        syncing=true;
        var minimum=Minimum;
        foreach(var key in new[]{"X","Y"}){inputs[key].Minimum=-1000000;inputs[key].Maximum=1000000;}
        inputs["W"].Maximum=(decimal)travel.X;inputs["H"].Maximum=(decimal)travel.Y;
        var values=new[]{profile.X,profile.Y,profile.Width>0 ? profile.Width : Math.Max(.001,minimum.X+travel.X-profile.X),profile.Height>0 ? profile.Height : Math.Max(.001,minimum.Y+travel.Y-profile.Y),profile.Amplitude,(double)profile.Columns,profile.Rows};
        var keys=new[]{"X","Y","W","H","Δ ±","Nx","Ny"};for(int i=0;i<keys.Length;i++)inputs[keys[i]].Value=(decimal)Math.Clamp(values[i],(double)inputs[keys[i]].Minimum,(double)inputs[keys[i]].Maximum);
        enabled.Checked=profile.Enabled;coordinates.SelectedIndex=(int)profile.Coordinates;syncing=false;
    }
    private void Changed()
    {
        if(syncing)return;var previousAmplitude=profile.Amplitude;var columns=(int)inputs["Nx"].Value;var rows=(int)inputs["Ny"].Value;
        var minimum=Minimum;profile.X=Math.Clamp((double)inputs["X"].Value,minimum.X,minimum.X+travel.X-.001);profile.Y=Math.Clamp((double)inputs["Y"].Value,minimum.Y,minimum.Y+travel.Y-.001);profile.Width=Math.Clamp((double)inputs["W"].Value,.001,minimum.X+travel.X-profile.X);profile.Height=Math.Clamp((double)inputs["H"].Value,.001,minimum.Y+travel.Y-profile.Y);profile.Amplitude=(double)inputs["Δ ±"].Value;profile.Enabled=enabled.Checked;
        if(columns!=profile.Columns||rows!=profile.Rows){profile.Columns=columns;profile.Rows=rows;profile.Generate(seed++);}
        else if(previousAmplitude!=profile.Amplitude)for(int i=0;i<profile.Values.Length;i++)profile.Values[i]=previousAmplitude>0 ? profile.Values[i]*profile.Amplitude/previousAmplitude : 0;
        LoadInputs();Save();
    }
    private void Save(bool rebuild=true)
    {
        engine.ConfigureSurface(profile);if(rebuild)RefreshGrid();canvas.Invalidate();
        hint.Text="X/Y — в выбранной системе координат; W/H — размеры области. Δ ± — отклонение от поверхности материала. Значения узлов редактируются в таблице; между ними — билинейная интерполяция.";
    }
    private void RefreshGrid()
    {
        syncing=true;grid.Columns.Clear();grid.Rows.Clear();
        var width=profile.Width>0 ? profile.Width : Minimum.X+travel.X-profile.X;var height=profile.Height>0 ? profile.Height : Minimum.Y+travel.Y-profile.Y;
        for(int c=0;c<profile.Columns;c++)grid.Columns.Add("X"+c,$"X {profile.X+width*c/(profile.Columns-1):0.##}");
        for(int r=0;r<profile.Rows;r++){grid.Rows.Add(Enumerable.Range(0,profile.Columns).Select(c=>(object)profile.Values[r*profile.Columns+c].ToString("0.000",CultureInfo.InvariantCulture)).ToArray());grid.Rows[r].HeaderCell.Value=$"Y {profile.Y+height*r/(profile.Rows-1):0.##}";}
        syncing=false;hint.Text="Редактируйте Δ узлов со знаком; «Создать рельеф» заполняет сетку. Синий — ниже, жёлтый — выше. Вне области поверхность ровная.";
    }
}

internal sealed class SurfaceCanvas(Func<SurfaceProfile> getProfile,Func<Vec> getTravel,Func<Vec> getOrigin) : Control
{
    public (int Column,int Row) Selected=(-1,-1);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var p=getProfile();var travel=getTravel();var origin=getOrigin();var minimum=new Vec(-travel.X,-travel.Y,0)-origin;var g=e.Graphics;
        var scale=(float)Math.Min(Math.Max(1,Width-80)/Math.Max(1,travel.X),Math.Max(1,Height-65)/Math.Max(1,travel.Y));
        var rect=new RectangleF(50,20,(float)travel.X*scale,(float)travel.Y*scale);
        using var bitmap=new Bitmap(160,120);
        for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++){
            var value=p.Delta(minimum.X+travel.X*x/(bitmap.Width-1),minimum.Y+travel.Y*(1-y/(double)(bitmap.Height-1)),travel.X,travel.Y);var t=p.Amplitude>0 ? value/p.Amplitude : 0;
            bitmap.SetPixel(x,y,t>=0 ? Color.FromArgb((int)(40+170*t),(int)(65+120*t),(int)(85-35*t)) : Color.FromArgb(40,(int)(65-35*t),(int)(85-150*t)));
        }
        g.DrawImage(bitmap,rect);using var border=new Pen(Color.Red){DashStyle=System.Drawing.Drawing2D.DashStyle.Dash};g.DrawRectangle(border,rect.X,rect.Y,rect.Width,rect.Height);
        var width=p.Width>0 ? p.Width : minimum.X+travel.X-p.X;var height=p.Height>0 ? p.Height : minimum.Y+travel.Y-p.Y;
        PointF Point(int c,int r)=>new(rect.Left+(float)(p.X-minimum.X+width*c/(p.Columns-1))*scale,rect.Bottom-(float)(p.Y-minimum.Y+height*r/(p.Rows-1))*scale);
        var saved=g.Save();g.SetClip(rect);
        using var line=new Pen(Color.FromArgb(120,190,205,220));using var nodes=new SolidBrush(Color.White);
        for(int r=0;r<p.Rows;r++)g.DrawLine(line,Point(0,r),Point(p.Columns-1,r));for(int c=0;c<p.Columns;c++)g.DrawLine(line,Point(c,0),Point(c,p.Rows-1));
        for(int r=0;r<p.Rows;r++)for(int c=0;c<p.Columns;c++){var point=Point(c,r);g.FillEllipse(nodes,point.X-2,point.Y-2,4,4);}
        if(Selected.Column>=0 && Selected.Column<p.Columns && Selected.Row>=0 && Selected.Row<p.Rows){var point=Point(Selected.Column,Selected.Row);using var selected=new Pen(Color.Gold,2);g.DrawEllipse(selected,point.X-6,point.Y-6,12,12);}
        g.Restore(saved);
        using var text=new SolidBrush(ForeColor);g.DrawString($"{minimum.X:0.##}, {minimum.Y:0.##}",Font,text,rect.Left,rect.Bottom+5);g.DrawString($"X {travel.X:0.##} мм",Font,text,rect.Right-85,rect.Bottom+5);g.DrawString($"Y {travel.Y:0.##}",Font,text,0,rect.Top);g.DrawString(p.Enabled ? $"Δ ±{p.Amplitude:0.###} мм" : "Неровности выключены",Font,text,rect.Left,rect.Bottom+25);
    }
    public SurfaceCanvas() : this(()=>new SurfaceProfile(),()=>new Vec(200,200,200),()=>new Vec(-200,-200,0)){}
    protected override void OnCreateControl(){base.OnCreateControl();Dock=DockStyle.Fill;DoubleBuffered=true;BackColor=Color.FromArgb(17,23,32);}
}
