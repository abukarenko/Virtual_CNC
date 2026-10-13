namespace VirtualCnc.App;

internal sealed class SensorIndicator : Control
{
    private bool active, pressed;
    private readonly bool momentary;
    private readonly Color activeColor;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get=>active; set {if(active==value)return;active=value;Invalidate();Changed?.Invoke();} }
    public event Action? Changed;
    public event Action<bool>? PressChanged;
    public SensorIndicator(string caption,Color color,bool momentary=false)
    {
        Text=caption;activeColor=color;this.momentary=momentary;Dock=DockStyle.Fill;Margin=Padding.Empty;TabStop=true;Cursor=Cursors.Hand;
        AccessibleName=caption;AccessibleRole=AccessibleRole.CheckButton;
        SetStyle(ControlStyles.Selectable|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
    }
    private void Press(bool value)
    {
        if(pressed==value)return;pressed=value;Active=value;PressChanged?.Invoke(value);
    }
    protected override void OnClick(EventArgs e) {base.OnClick(e);Focus();if(!momentary)Active=!Active;}
    protected override void OnMouseDown(MouseEventArgs e) {base.OnMouseDown(e);if(momentary && e.Button==MouseButtons.Left){Focus();Capture=true;Press(true);}}
    protected override void OnMouseUp(MouseEventArgs e) {base.OnMouseUp(e);if(momentary && e.Button==MouseButtons.Left){Press(false);Capture=false;}}
    protected override void OnMouseCaptureChanged(EventArgs e) {base.OnMouseCaptureChanged(e);if(momentary && !Capture)Press(false);}
    protected override void OnLostFocus(EventArgs e) {base.OnLostFocus(e);if(momentary){Press(false);Capture=false;}}
    protected override void OnKeyDown(KeyEventArgs e) {base.OnKeyDown(e);if(e.KeyCode==Keys.Space){if(momentary)Press(true);else Active=!Active;e.Handled=true;}}
    protected override void OnKeyUp(KeyEventArgs e) {base.OnKeyUp(e);if(momentary && e.KeyCode==Keys.Space){Press(false);e.Handled=true;}}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var square=new Rectangle((Width-24)/2,3,24,24);
        using var fill=new SolidBrush(Active ? activeColor : Color.Gray);e.Graphics.FillRectangle(fill,square);
        using var border=new Pen(Focused ? Color.White : Color.FromArgb(10,15,22));e.Graphics.DrawRectangle(border,square);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,30,Width,20),ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
    }
}
