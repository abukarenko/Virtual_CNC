using VirtualCnc.Core;
namespace VirtualCnc.App;

internal sealed class ZView : Control
{
    private MachineSnapshot? scene;
    public ZView(){DoubleBuffered=true;BackColor=Color.FromArgb(17,23,32);}
    public void UpdateScene(MachineSnapshot snapshot){scene=snapshot;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(scene is not {} s)return;
        var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var travel=Math.Max(1,s.MaxTravel.Z);var top=130f;var bottom=Math.Max(top+30,Height-65f);var x=Width/2f;
        float Y(double z)=>(float)(top-z/travel*(bottom-top));
        using var label=new SolidBrush(Color.FromArgb(225,233,242));using var outline=new Pen(Color.LightGray,1);
        using var guide=new Pen(Color.FromArgb(95,120,140),1){DashStyle=System.Drawing.Drawing2D.DashStyle.Dot};
        using var cyan=new Pen(Color.DeepSkyBlue,1);using var gold=new SolidBrush(Color.Goldenrod);
        using var stock=new SolidBrush(Color.FromArgb(95,85,43));using var stockBorder=new Pen(Color.DarkKhaki);
        using var red=new SolidBrush(Color.Red);using var yellow=new SolidBrush(Color.Gold);
        g.DrawString("Z · вид сбоку",Font,label,8,8);
        g.DrawPolygon(cyan,new PointF[]{new(x-24,32),new(x+24,32),new(x+24,49),new(x,59),new(x-24,49)});
        TextRenderer.DrawText(g,"LIM Z",Font,new Rectangle((int)x-24,32,48,18),s.Limits.Contains('Z') ? Color.Red : Color.DeepSkyBlue,TextFormatFlags.HorizontalCenter);
        g.DrawString("0.000",Font,label,8,top-6);
        g.DrawLine(guide,x,59,x,bottom);
        var surface=Y(-travel+Math.Clamp(s.MaterialThickness+s.SurfaceDelta,0,travel));var width=Math.Max(30,Width-22);
        g.FillRectangle(stock,11,surface,width,bottom-surface);g.DrawRectangle(stockBorder,11,surface,width,bottom-surface);
        g.DrawString($"H={s.MaterialThickness:0.###} Δ={s.SurfaceDelta:+0.##;-0.##;0}",Font,label,11,surface-22);
        TextRenderer.DrawText(g,"workpiece",Font,new Rectangle(11,(int)surface+3,width,22),Color.Gold,TextFormatFlags.HorizontalCenter);
        g.FillRectangle(gold,6,bottom,Width-12,7);
        g.DrawString($"Table {-travel:0.000}",Font,label,8,bottom+12);
        var tip=Y((s.PhysicalPosition ?? s.MachinePosition).Z);var saved=g.Save();g.SetClip(new RectangleF(0,60,Width,bottom-60+1));
        g.DrawRectangle(outline,x-18,tip-70,36,38);g.DrawRectangle(outline,x-10,tip-28,20,14);
        g.DrawLine(outline,x,tip-14,x,tip);g.FillEllipse(red,x-3,tip-3,6,6);g.Restore(saved);
        g.FillEllipse(yellow,Width-9,Math.Clamp(tip,top,bottom)-3,6,6);
        g.DrawString($"MZ {s.MachinePosition.Z:0.000}",Font,label,8,Height-24);
    }
}
