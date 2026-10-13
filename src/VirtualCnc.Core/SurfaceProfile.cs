namespace VirtualCnc.Core;

public enum SurfaceCoordinates { Table, Machine, Work }

/// <summary>Signed height offsets in the selected coordinate system, bilinearly interpolated.</summary>
public sealed class SurfaceProfile
{
    public bool Enabled {get;set;}
    public SurfaceCoordinates Coordinates {get;set;}
    public double X {get;set;}
    public double Y {get;set;}
    public double Width {get;set;} // Zero means full remaining table width.
    public double Height {get;set;}
    public double Amplitude {get;set;}=1;
    public int Columns {get;set;}=5;
    public int Rows {get;set;}=5;
    public double[] Values {get;set;}=new double[25];
    public SurfaceProfile Copy()=>new(){Enabled=Enabled,Coordinates=Coordinates,X=X,Y=Y,Width=Width,Height=Height,Amplitude=Amplitude,Columns=Columns,Rows=Rows,Values=(double[])Values.Clone()};
    public double Delta(double tableX,double tableY,double travelX,double travelY)
    {
        if(!Enabled || Columns<2 || Rows<2 || Values.Length!=Rows*Columns)return 0;
        var width=Width>0 ? Width : travelX-X;var height=Height>0 ? Height : travelY-Y;
        if(width<=0 || height<=0 || tableX<X || tableY<Y || tableX>X+width || tableY>Y+height)return 0;
        var u=(tableX-X)/width*(Columns-1);var v=(tableY-Y)/height*(Rows-1);
        var col=Math.Min(Columns-2,(int)u);var row=Math.Min(Rows-2,(int)v);u-=col;v-=row;
        var lower=Values[row*Columns+col]*(1-u)+Values[row*Columns+col+1]*u;
        var upper=Values[(row+1)*Columns+col]*(1-u)+Values[(row+1)*Columns+col+1]*u;
        return Math.Clamp(lower*(1-v)+upper*v,-Amplitude,Amplitude);
    }
    public void Generate(int seed)
    {
        var random=new Random(seed);Values=new double[Rows*Columns];
        for(int row=0;row<Rows;row++)for(int col=0;col<Columns;col++)
            Values[row*Columns+col]=(row==0 || col==0 || row==Rows-1 || col==Columns-1) ? 0 : (random.NextDouble()*2-1)*Amplitude;
    }
}
