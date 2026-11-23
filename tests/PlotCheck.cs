using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PlotCheck
{
    const int Width=202,Height=130;
    static Sample Row(DateTime time,double value)
    {
        var row=new Sample {Time=time};
        row.Values[(int)K.CpuLoad]=value;
        return row;
    }
    static bool HasTrace(byte[] pixels,int x,int y)
    {
        for(int j=y-2;j<=y+2;j++)
            for(int i=x-2;i<=x+2;i++){
                int n=4*(j*Width+i);
                if(pixels[n+2]>190 && pixels[n+1]<100 && pixels[n]<100)return true;
            }
        return false;
    }
    [STAThread] static void Main()
    {
        var start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        var chart=new Plot {End=start.AddHours(24),Minutes=1440,Unit="%",MaxGapSeconds=1,
            Series=new[]{new Trace(K.CpuLoad,"CPU","#FF0000")}};
        // Three consecutive seconds fall in one pixel. The fourth sample stands alone.
        chart.Rows.Add(Row(start.AddHours(6),10));
        chart.Rows.Add(Row(start.AddHours(6).AddSeconds(1),95));
        chart.Rows.Add(Row(start.AddHours(6).AddSeconds(2),10));
        chart.Rows.Add(Row(start.AddHours(18),50));
        chart.Measure(new Size(Width,Height));chart.Arrange(new Rect(0,0,Width,Height));
        var bitmap=new RenderTargetBitmap(Width,Height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(chart);
        var pixels=new byte[Width*Height*4];bitmap.CopyPixels(pixels,4*Width,0);
        if(!HasTrace(pixels,79,18) || !HasTrace(pixels,79,94))throw new Exception("Lost an intra-pixel extremum");
        if(!HasTrace(pixels,153,58))throw new Exception("Lost isolated sample");
        if(HasTrace(pixels,116,76) || Plot.Nearest(chart.Rows,start.AddHours(18).AddMinutes(-1),start,chart.End,1)!=null)
            throw new Exception("Gap contains fabricated measurement");
        if(Plot.Nearest(chart.Rows,start.AddHours(18),start,chart.End,1)!=chart.Rows[3])
            throw new Exception("Cursor missed isolated sample");
        chart.LoadTrend=true;chart.RefreshPlot();chart.UpdateLayout();
        bitmap=new RenderTargetBitmap(Width,Height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(chart);bitmap.CopyPixels(pixels,4*Width,0);
        if(!HasTrace(pixels,79,69) || HasTrace(pixels,79,18) || !HasTrace(pixels,153,58))
            throw new Exception("Load overview should emphasize the mean and preserve isolated observations");
        if(HasTrace(pixels,116,64))throw new Exception("Load overview connected across missing data");
        Console.WriteLine("PASS: daily plot preserves a one-second peak, isolated measurement and recording gap.");
    }
}
