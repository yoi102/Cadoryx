using System.Globalization;

namespace Cadoryx.Kernel.Occt;

/// <summary>Checks only known unit-axis fields in OCCT text BRep V3. This is a
/// roundtrip witness for the same indexed graph, never a shape matching function.</summary>
public static class BrepDirectionRoundtrip
{
    private const double AxisRoundoff=8*2.2204460492503131e-16;
    public static bool Matches(string before,string after)
    {
        if(before==after)return true;
        var a=before.Replace("\r\n","\n").Split('\n');var b=after.Replace("\r\n","\n").Split('\n');
        if(a.Length!=b.Length||!a.Contains("CASCADE Topology V3, (c) Open Cascade"))return false;
        string section="";
        for(int line=0;line<a.Length;line++)
        {
            var x=a[line].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            var y=b[line].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(x.Length!=y.Length)return false;
            if(x.Length==2&&(x[0] is "Curve2ds" or "Curves" or "Surfaces"))
            {
                if(!x.SequenceEqual(y)||!int.TryParse(x[1],out int count)||count<0)return false;
                section=x[0];continue;
            }
            bool record=section=="Curves"&&((x.Length==7&&x[0]=="1")||(x.Length==14&&x[0]=="2")||
                (x.Length==15&&x[0]=="3")||(x.Length==3&&x[0]=="8"))||
                section=="Surfaces"&&((x.Length==13&&x[0]=="1")||(x.Length==14&&x[0]=="2"));
            // An unsupported/multiline record closes the section for tolerant comparison.
            // Never interpret control-point or connectivity rows as analytic records.
            if(!record)section="";
            if(x.SequenceEqual(y))continue;
            // Circle/ellipse axes are three unit vectors after an exact origin.
            // Only single-line analytic records with an exact arity are supported.
            bool axes=record&&section=="Curves"&&((x[0]=="2"&&x.Length==14)||(x[0]=="3"&&x.Length==15))||
                record&&section=="Surfaces"&&((x[0]=="1"&&x.Length==13)||(x[0]=="2"&&x.Length==14));
            if(!axes)return false;
            for(int column=0;column<x.Length;column++)
            {
                if(x[column]==y[column])continue;
                if(column is <4 or >12||!Number(x[column],out double u)||!Number(y[column],out double v)||
                    Math.Abs(u)>1+AxisRoundoff||Math.Abs(v)>1+AxisRoundoff||Math.Abs(u-v)>AxisRoundoff)return false;
            }
            for(int start=4;start<=10;start+=3)
            {
                double n=0,m=0;
                for(int j=start;j<start+3;j++)
                {
                    if(!Number(x[j],out double u)||!Number(y[j],out double v))return false;
                    n+=u*u;m+=v*v;
                }
                if(Math.Abs(n-1)>AxisRoundoff*4||Math.Abs(m-1)>AxisRoundoff*4)return false;
            }
        }
        return true;
    }
    private static bool Number(string text,out double value)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);
}
