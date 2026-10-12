namespace VirtualCnc.App;

internal sealed class SensorIndicator : Control
{
    private bool active;
    private readonly Color activeColor;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get=>active; set {if(active==value)return;active=value;Invalidate();Changed?.Invoke();} }
    public event Action? Changed;
    public SensorIndicator(string caption,Color color)
    {
        Text=caption;activeColor=color;Dock=DockStyle.Fill;Margin=Padding.Empty;TabStop=true;Cursor=Cursors.Hand;
        AccessibleName=caption;AccessibleRole=AccessibleRole.CheckButton;
        SetStyle(ControlStyles.Selectable|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
    }
    protected override void OnClick(EventArgs e) {base.OnClick(e);Focus();Active=!Active;}
    protected override void OnKeyDown(KeyEventArgs e) {base.OnKeyDown(e);if(e.KeyCode==Keys.Space){Active=!Active;e.Handled=true;}}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var square=new Rectangle((Width-24)/2,3,24,24);
        using var fill=new SolidBrush(Active ? activeColor : Color.Gray);e.Graphics.FillRectangle(fill,square);
        using var border=new Pen(Focused ? Color.White : Color.FromArgb(10,15,22));e.Graphics.DrawRectangle(border,square);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,30,Width,20),ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
    }
}
