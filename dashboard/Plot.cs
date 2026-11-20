using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;

internal sealed class Trace
{
    public K Key; public string Label; public Brush Color;
    public Trace(K key,string label,string color) {Key=key;Label=label;Color=StyleKit.B(color);}
}

internal sealed class Plot : FrameworkElement
{
    public bool Mini,Compact,TimeLabels=true;
    public List<Sample> Rows=new List<Sample>();
    public Trace[] Series=new Trace[0];
    public DateTime End=DateTime.UtcNow;
    public int Minutes=15;
    public string Unit="°C";
    Point? pointer;
    public event Action<DateTime?> Inspect;
    public DateTime? SharedTime;
    DrawingGroup background;
    Sample[] visible=new Sample[0];
    double minY,maxY;
    public void RefreshPlot(){background=null;InvalidateVisual();}
    static readonly Brush GridInk=StyleKit.B("#E3E9E7"),TextInk=StyleKit.B("#65736D");
    public Plot()
    {
        MouseMove+=delegate(object s,MouseEventArgs e){if(!Mini){pointer=e.GetPosition(this);if(Inspect!=null)Inspect(TimeAt(pointer.Value.X));InvalidateVisual();}};
        MouseLeave+=delegate{pointer=null;if(Inspect!=null)Inspect(null);InvalidateVisual();};
        SnapsToDevicePixels=true;
    }
    void Label(DrawingContext d,string s,double x,double y,Brush color,double size,bool right=false)
    {
        var t=new FormattedText(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
            new Typeface(StyleKit.Font,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        d.DrawText(t,new Point(right?x-t.Width:x,y));
    }
    DateTime TimeAt(double x){return End.AddMinutes(-Minutes+Minutes*Math.Max(0,Math.Min(1,(x-42)/Math.Max(1,ActualWidth-54))));}
    protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);background=null;}
    internal static int Lower(List<Sample> rows,DateTime t)
    {
        int l=0,r=rows.Count;while(l<r){int m=l+(r-l)/2;if(rows[m].Time<t)l=m+1;else r=m;}return l;
    }
    protected override void OnRender(DrawingContext d)
    {
        if(background==null){background=new DrawingGroup();using(var draw=background.Open())DrawBase(draw);}
        d.DrawDrawing(background);
        if(Mini || visible.Length==0 || Series.Length==0 || (!pointer.HasValue && !SharedTime.HasValue))return;
        DateTime at=pointer.HasValue?TimeAt(pointer.Value.X):SharedTime.Value;
        int l=0,r=visible.Length-1;
        while(l<r){int mid=(l+r)/2;if(visible[mid].Time<at)l=mid+1;else r=mid;}
        if(l>0 && (at-visible[l-1].Time).Duration()<(visible[l].Time-at).Duration())l--;
        var near=visible[l];double width=ActualWidth-54;
        double xx=42+(near.Time-End.AddMinutes(-Minutes)).TotalMinutes*width/Minutes;
        if(Math.Abs((at-near.Time).TotalSeconds)>Math.Max(4,Minutes*60/Math.Max(1,width)*5))return;
        double y0=Compact?3:13,y1=ActualHeight-(TimeLabels?27:4);
        d.DrawLine(new Pen(TextInk,1),new Point(xx,y0),new Point(xx,y1));
        if(pointer.HasValue && !Compact){
            double bw=Math.Min(176,width),bx=Math.Max(42,Math.Min(xx+10,ActualWidth-12-bw));
            d.DrawRoundedRectangle(StyleKit.B("#F9FCFB"),new Pen(GridInk,1),new Rect(bx,y0+3,bw,29+Series.Length*17),6,6);
            Label(d,near.Time.ToLocalTime().ToString("HH:mm:ss"),bx+10,y0+7,TextInk,12);
            for(int i=0;i<Series.Length;i++){var tr=Series[i];Label(d,tr.Label,bx+10,y0+25+i*17,tr.Color,12);
                Label(d,StyleKit.Value(near[tr.Key],Unit=="°C"||Unit=="dBFS"?1:0),bx+bw-10,y0+25+i*17,TextInk,12,true);}
        }
        foreach(var tr in Series)if(!double.IsNaN(near[tr.Key]) && maxY>minY)
            d.DrawEllipse(tr.Color,null,new Point(xx,y1-(near[tr.Key]-minY)/(maxY-minY)*(y1-y0)),3,3);
    }
    static void Flush(StreamGeometryContext g,Point first,Point low,Point high,Point last)
    {
        if(low.X<high.X){g.LineTo(low,true,false);g.LineTo(high,true,false);}
        else {g.LineTo(high,true,false);g.LineTo(low,true,false);}
        if(last!=first)g.LineTo(last,true,false);
    }
    void DrawBase(DrawingContext d)
    {
        double w=ActualWidth,h=ActualHeight;
        if(w<24 || h<16) return;
        double x0=Mini?1:42, x1=w-(Mini?1:12), y0=Mini?2:Compact?3:13, y1=h-(Mini?2:TimeLabels?27:4);
        if (x1<=x0 || y1<=y0) return;
        d.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,w,h));
        DateTime start=End.AddMinutes(-Minutes);
        int firstIndex=Lower(Rows,start),endIndex=Lower(Rows,End.AddTicks(1));
        var rows=Rows.GetRange(firstIndex,endIndex-firstIndex);visible=rows.ToArray();
        double min=double.PositiveInfinity,max=double.NegativeInfinity;
        foreach(var item in rows)foreach(var tr in Series){double v=item[tr.Key];if(v<min)min=v;if(v>max)max=v;}
        double lo=0,hi=100;
        if(min<=max)
        {
            if (Mini) {lo=min-1;hi=max+1;}
            else if(Unit!="%")
            {
                double step=Unit=="об/мин"?(max>6000?2000:500):Unit=="dBFS"?5:10;
                lo=Unit=="об/мин"?0:Math.Floor((min-1)/step)*step;
                hi=Math.Max(lo+step,Math.Ceiling((max+1)/step)*step);
            }
        }
        minY=lo;maxY=hi;
        Func<DateTime,double> x=t=>x0+(t-start).TotalMinutes/Minutes*(x1-x0);
        Func<double,double> y=v=>y1-(v-lo)/(hi-lo)*(y1-y0);
        if(!Mini)
        {
            int ticks=Compact?1:4;
            for(int i=0;i<=ticks;i++)
            {
                double yy=y0+i*(y1-y0)/ticks;
                d.DrawLine(new Pen(GridInk,1),new Point(x0,yy),new Point(x1,yy));
                Label(d,(hi-i*(hi-lo)/ticks).ToString("0.#",CultureInfo.GetCultureInfo("ru-RU")),x0-8,Math.Max(0,yy-8),TextInk,11,true);
            }
            for(int i=0;TimeLabels && i<=4;i++)
            {
                DateTime t=start.AddMinutes(Minutes*i/4.0);
                double xx=x(t);
                Label(d,t.ToLocalTime().ToString(Minutes<=1?"HH:mm:ss":"HH:mm"),i==4?xx:(i==0?xx:xx-14),y1+7,TextInk,11,i==4);
            }
        }
        if(min>max)
        {
            if(!Mini) Label(d,Series.Length==0?"Выберите датчик":"Нет измерений за этот период",x0+10,y0+12,TextInk,14);
            return;
        }
        foreach(var trace in Series)
        {
            var geom=new StreamGeometry();
            using(var g=geom.Open())
            {
                DateTime last=DateTime.MinValue; bool open=false;
                Point first=new Point(),low=first,high=first,tail=first;int pixel=-1;
                foreach(var row in rows)
                {
                    double v=row[trace.Key];
                    if(double.IsNaN(v)){if(open)Flush(g,first,low,high,tail);open=false;continue;}
                    var p=new Point(x(row.Time),y(v));
                    bool gap=!open || (row.Time-last).TotalSeconds>4;
                    int bucket=(int)p.X;
                    if(gap || pixel!=bucket){
                        if(open)Flush(g,first,low,high,tail);
                        if(gap)g.BeginFigure(p,false,false);else g.LineTo(p,true,false);
                        first=low=high=tail=p;pixel=bucket;
                    }else{if(p.Y<low.Y)low=p;if(p.Y>high.Y)high=p;tail=p;}
                    last=row.Time;open=true;
                }
                if(open)Flush(g,first,low,high,tail);
            }
            geom.Freeze(); d.DrawGeometry(null,new Pen(trace.Color,Mini?1.5:2),geom);
        }
    }
}
