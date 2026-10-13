using VirtualCnc.Core;

namespace VirtualCnc.App;

internal sealed class PathView : Control
{
    private readonly List<MotionSegment> points=[];
    private readonly XYViewport camera=new();
    private Vec current, workPosition, workOffset;
    private double travelZ=200;
    private RectangleF Plot => new(38,32,Math.Max(1,Width-62),Math.Max(1,Height-78));
    public PathView()
    {
        DoubleBuffered=true; TabStop=true; BackColor=Color.FromArgb(17,23,32);
        SetStyle(ControlStyles.Selectable,true);
    }
    public void UpdateScene(MachineSnapshot snapshot)
    {
        current=snapshot.PhysicalPosition ?? snapshot.MachinePosition;workPosition=snapshot.WorkPosition;workOffset=current-workPosition; travelZ=Math.Max(1,snapshot.MaxTravel.Z);
        camera.SetTravel(snapshot.MaxTravel.X,snapshot.MaxTravel.Y);
        Invalidate();
    }
    public void Add(MotionSegment segment)
    {
        if(segment.Start==segment.End) return;
        // Keep the complete machining history until an explicit reset.
        points.Add(segment);
    }
    public void Clear() { points.Clear(); Invalidate(); }
    public void FitMachine() { camera.Fit(); Invalidate(); }
    private void SizeCamera() => camera.SetSize(Plot.Width,Plot.Height);
    private PointF Project(Vec p)
    {
        var screen=camera.Project(p); return new((float)screen.X+Plot.Left,(float)screen.Y+Plot.Top);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); SizeCamera();
        camera.ZoomAt(e.X-Plot.Left,e.Y-Plot.Top,Math.Pow(1.2,e.Delta/120.0)); Invalidate();
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if(e.Button==MouseButtons.Left) FitMachine(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); SizeCamera();
        var g=e.Graphics; g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var plot=Plot; var saved=g.Save(); g.SetClip(plot);
        var topLeft=Project(new(-camera.TravelX,0,0)); var bottomRight=Project(new(0,-camera.TravelY,0));
        var machineRect=RectangleF.FromLTRB(topLeft.X,topLeft.Y,bottomRight.X,bottomRight.Y);
        using var bed=new SolidBrush(Color.FromArgb(23,32,44)); g.FillRectangle(bed,machineRect);
        using var frame=new Pen(Color.Red,1.5f) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash };

        using var grid=new Pen(Color.FromArgb(39,51,66));
        using var text=new SolidBrush(Color.FromArgb(165,182,202));
        var corner0=camera.Unproject(0,0)-workOffset;
        var corner1=camera.Unproject(plot.Width,plot.Height)-workOffset;
        var desired=75/camera.Scale; var power=Math.Pow(10,Math.Floor(Math.Log10(desired)));
        var fraction=desired/power; var gridStep=(fraction<=1 ? 1 : fraction<=2 ? 2 : fraction<=5 ? 5 : 10)*power;
        for(var x=Math.Ceiling(corner0.X/gridStep)*gridStep;x<=corner1.X;x+=gridStep)
        {
            var screen=Project(new Vec(x,0,0)+workOffset);
            g.DrawLine(grid,screen.X,plot.Top,screen.X,plot.Bottom);
            g.DrawString(x.ToString("0.###"),Font,text,screen.X+3,plot.Bottom-19);
        }
        for(var y=Math.Ceiling(corner1.Y/gridStep)*gridStep;y<=corner0.Y;y+=gridStep)
        {
            var screen=Project(new Vec(0,y,0)+workOffset);
            g.DrawLine(grid,plot.Left,screen.Y,plot.Right,screen.Y);
            g.DrawString(y.ToString("0.###"),Font,text,plot.Left+3,screen.Y+2);
        }
        var origin=Project(workOffset);
        using var axes=new Pen(Color.FromArgb(117,143,170),1.5f);
        g.DrawLine(axes,plot.Left,origin.Y,plot.Right,origin.Y); g.DrawLine(axes,origin.X,plot.Top,origin.X,plot.Bottom);
        using var trace=new Pen(Color.FromArgb(58,206,164),2);
        using var rapid=new Pen(Color.FromArgb(77,170,170,170),2);
        foreach(var segment in points) g.DrawLine(segment.Rapid ? rapid : trace,Project(segment.Start),Project(segment.End));
        g.DrawRectangle(frame,machineRect.X,machineRect.Y,machineRect.Width,machineRect.Height);
        var tool=Project(current); using var toolBrush=new SolidBrush(Color.FromArgb(255,188,81));
        var diameter=(float)(5+15*Math.Clamp((current.Z+travelZ)/travelZ,0,1));
        g.FillEllipse(toolBrush,tool.X-diameter/2,tool.Y-diameter/2,diameter,diameter);
        g.Restore(saved);
        g.DrawString($"XY · {camera.TravelX:0.###} × {camera.TravelY:0.###} мм · Z {travelZ:0.###} мм · масштаб {camera.Zoom:0.##}×",Font,text,15,8);
        g.DrawString($"WPos {workPosition.Format()} · колесо: масштаб",Font,text,15,Height-30);
    }
}
