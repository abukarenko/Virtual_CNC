namespace VirtualCnc.Core;

/// <summary>XY camera in machine millimeters.</summary>
public sealed class XYViewport
{
    public double Width { get; private set; } = 1;
    public double Height { get; private set; } = 1;
    public double TravelX { get; private set; } = 200;
    public double TravelY { get; private set; } = 200;
    public double Zoom { get; private set; } = 1;
    public double Scale => .88*Math.Min(Width/TravelX,Height/TravelY)*Zoom;
    private double panX, panY;
    public void SetSize(double width,double height) { Width=Math.Max(1,width); Height=Math.Max(1,height); }
    public void SetTravel(double x,double y)
    {
        x=Math.Max(1,x); y=Math.Max(1,y);
        if(x==TravelX && y==TravelY) return;
        TravelX=x; TravelY=y; Fit();
    }
    public void Fit() { Zoom=1; panX=panY=0; }
    public Vec Project(Vec machine) => new(Width/2+(machine.X+TravelX/2)*Scale+panX,Height/2-(machine.Y+TravelY/2)*Scale+panY,0);
    /// <summary>Table coordinates: zero at the lower left, positive X right and Y up.</summary>
    public Vec ProjectTable(Vec table) => Project(table-new Vec(TravelX,TravelY,0));
    public Vec UnprojectTable(double x,double y) => Unproject(x,y)+new Vec(TravelX,TravelY,0);
    public Vec Unproject(double x,double y) => new((x-Width/2-panX)/Scale-TravelX/2,-(y-Height/2-panY)/Scale-TravelY/2,0);
    private void Pan(double dx,double dy) { panX+=dx; panY+=dy; }
    public void ZoomAt(double x,double y,double factor)
    {
        var anchor=Unproject(x,y);
        Zoom=Math.Clamp(Zoom*factor,.05,200);
        var projected=Project(anchor);
        Pan(x-projected.X,y-projected.Y);
    }
}
