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
    public bool Mini,Compact,LoadTrend,TimeLabels=true;
    public List<Sample> Rows=new List<Sample>();
    public Trace[] Series=new Trace[0];
    public DateTime End=DateTime.UtcNow;
    public int Minutes=15;
    public string Unit="°C";
    Point? pointer;
    public event Action<DateTime?> Inspect;
    public DateTime? SharedTime;
    DrawingGroup background;
    public double MaxGapSeconds=4;
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
    internal static Sample Nearest(List<Sample> rows,DateTime at,DateTime start,DateTime end,double maxGapSeconds)
    {
        if(rows.Count==0 || at<start || at>end)return null;
        int i=Lower(rows,at);
        if(i==rows.Count || (i>0 && at-rows[i-1].Time<rows[i].Time-at))--i;
        var row=rows[i];
        return row.Time>=start && row.Time<=end && Math.Abs((row.Time-at).TotalSeconds)<=maxGapSeconds/2 ? row : null;
    }
    protected override void OnRender(DrawingContext d)
    {
        if(background==null){background=new DrawingGroup();using(var draw=background.Open())DrawBase(draw);}
        d.DrawDrawing(background);
        if(Mini || ActualWidth<=54 || Series.Length==0 || (!pointer.HasValue && !SharedTime.HasValue))return;
        DateTime at=pointer.HasValue?TimeAt(pointer.Value.X):SharedTime.Value;
        var start=End.AddMinutes(-Minutes);
        if(at<start || at>End)return;
        var near=Nearest(Rows,at,start,End,MaxGapSeconds);
        double width=ActualWidth-54;
        double xx=42+((near==null?at:near.Time)-start).TotalMinutes*width/Minutes;
        double y0=Compact?3:13,y1=ActualHeight-(TimeLabels?27:4);
        d.DrawLine(new Pen(TextInk,1),new Point(xx,y0),new Point(xx,y1));
        if(near==null){
            if(!Compact)Label(d,"Нет замера",Math.Max(42,Math.Min(xx+6,ActualWidth-82)),y0+7,TextInk,12);
            return;
        }
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
        Point a=low.X<=high.X?low:high,b=low.X<=high.X?high:low;
        if(a!=first)g.LineTo(a,true,false);
        if(b!=a)g.LineTo(b,true,false);
        if(last!=b)g.LineTo(last,true,false);
    }
    internal int LoadIntervalSeconds {get{return Minutes<=1?1:Minutes<=5?5:Minutes<=15?10:Minutes<=60?30:Minutes<=360?180:900;}}
    void DrawLoadTrend(DrawingContext d,Trace trace,int index,int endIndex,Func<DateTime,double> x,Func<double,double> y)
    {
        // Anchor intervals to the clock so the line stays still between live updates.
        long interval=TimeSpan.TicksPerSecond*LoadIntervalSeconds;
        var line=new Pen(trace.Color,2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        var pale=trace.Color.Clone();pale.Opacity=0.29;pale.Freeze();
        var range=new Pen(pale,1.5);
        Point? previous=null;
        DateTime previousEnd=DateTime.MinValue;
        while(index<endIndex){
            var first=Rows[index];
            double value=first[trace.Key];
            if(double.IsNaN(value)){previous=null;index++;continue;}
            long bucket=first.Time.Ticks/interval;
            var last=first;
            double sum=value,min=value,max=value;
            int count=1;
            index++;
            while(index<endIndex){
                var row=Rows[index];
                value=row[trace.Key];
                if(double.IsNaN(value) || row.Time.Ticks/interval!=bucket || (row.Time-last.Time).TotalSeconds>MaxGapSeconds)break;
                sum+=value;min=Math.Min(min,value);max=Math.Max(max,value);
                count++;last=row;index++;
            }
            var mid=first.Time.AddTicks((last.Time-first.Time).Ticks/2);
            var p=new Point(x(mid),y(sum/count));
            d.DrawLine(range,new Point(p.X,y(min)),new Point(p.X,y(max)));
            if(previous.HasValue && (first.Time-previousEnd).TotalSeconds<=MaxGapSeconds)
                d.DrawLine(line,previous.Value,p);
            else d.DrawEllipse(trace.Color,null,p,1.3,1.3);
            previous=p;previousEnd=last.Time;
        }
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
        double min=double.PositiveInfinity,max=double.NegativeInfinity;
        for(int i=firstIndex;i<endIndex;i++)foreach(var tr in Series){double v=Rows[i][tr.Key];if(v<min)min=v;if(v>max)max=v;}
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
            DateTime previous=start;
            for(int i=firstIndex;i<=endIndex;i++){
                var next=i==endIndex?End:Rows[i].Time;
                if((next-previous).TotalSeconds>MaxGapSeconds){
                    double left=x(previous),right=x(next);
                    if(right-left>=2){
                        d.DrawRectangle(StyleKit.B("#F1F3F2"),null,new Rect(left,y0,right-left,y1-y0));
                        if(min<=max && right-left>120 && (!Compact || TimeLabels))
                            Label(d,"Нет записи",(left+right)/2-27,(y0+y1)/2-8,TextInk,12);
                    }
                }
                previous=next;
            }
            int ticks=LoadTrend?2:Compact?1:4;
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
                string format=Minutes>=1440 && (i==0 || i==4)?"dd.MM HH:mm":Minutes<=1?"HH:mm:ss":"HH:mm";
                Label(d,t.ToLocalTime().ToString(format),i==4?xx:(i==0?xx:xx-14),y1+7,TextInk,11,i==4);
            }
        }
        if(min>max)
        {
            if(!Mini) Label(d,Series.Length==0?"Выберите датчик":"Нет измерений за этот период",x0+10,y0+12,TextInk,14);
            return;
        }
        d.PushClip(new RectangleGeometry(new Rect(x0-1,y0-1,x1-x0+2,y1-y0+2)));
        foreach(var trace in Series)
        {
            if(LoadTrend){DrawLoadTrend(d,trace,firstIndex,endIndex,x,y);continue;}
            var geom=new StreamGeometry();
            using(var g=geom.Open())
            {
                DateTime last=DateTime.MinValue; bool open=false;
                Point first=new Point(),low=first,high=first,tail=first;int pixel=-1;
                int runLength=0;
                for(int i=firstIndex;i<endIndex;i++)
                {
                    var row=Rows[i];
                    double v=row[trace.Key];
                    bool valid=!double.IsNaN(v);
                    if(open && (!valid || (row.Time-last).TotalSeconds>MaxGapSeconds)){
                        Flush(g,first,low,high,tail);
                        if(runLength==1)d.DrawEllipse(trace.Color,null,tail,1.5,1.5);
                        open=false;
                    }
                    if(!valid)continue;
                    var p=new Point(x(row.Time),y(v));
                    int bucket=(int)p.X;
                    if(!open || pixel!=bucket){
                        if(open)Flush(g,first,low,high,tail);
                        if(!open){g.BeginFigure(p,false,false);runLength=0;}else g.LineTo(p,true,false);
                        first=low=high=tail=p;pixel=bucket;
                    }else{if(p.Y<low.Y)low=p;if(p.Y>high.Y)high=p;tail=p;}
                    last=row.Time;open=true;runLength++;
                }
                if(open){
                    Flush(g,first,low,high,tail);
                    if(runLength==1)d.DrawEllipse(trace.Color,null,tail,1.5,1.5);
                }
            }
            geom.Freeze();
            var pen=new Pen(trace.Color,Mini?1.5:2){LineJoin=PenLineJoin.Round};
            d.DrawGeometry(null,pen,geom);
        }
        d.Pop();
    }
}
