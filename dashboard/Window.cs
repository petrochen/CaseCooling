using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

internal static class StyleKit
{
    static readonly Dictionary<string,SolidColorBrush> paints=new Dictionary<string,SolidColorBrush>();
    public static readonly FontFamily Font=new FontFamily(new Uri(AppDomain.CurrentDomain.BaseDirectory),"./Fonts/#Source Sans 3, Segoe UI");
    public static SolidColorBrush B(string color)
    {
        SolidColorBrush b;
        if(!paints.TryGetValue(color,out b)) { b=(SolidColorBrush)new BrushConverter().ConvertFromString(color); b.Freeze(); paints[color]=b; }
        return b;
    }
    public static string Value(double v,int digits) { return double.IsNaN(v)?"—":v.ToString("F"+digits,CultureInfo.GetCultureInfo("ru-RU")); }
    public static TextBlock Text(string value,double size,string color,bool bold=false)
    {
        return new TextBlock { Text=value,FontFamily=Font,FontSize=size,Foreground=B(color),
            FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center };
    }
    public static Border Box(UIElement child,string fill,double radius,Thickness padding,string line=null)
    {
        return new Border {Child=child,Background=B(fill),CornerRadius=new CornerRadius(radius),Padding=padding,
            BorderBrush=line==null?null:B(line),BorderThickness=new Thickness(line==null?0:1)};
    }
    public static Grid Columns(params double[] weights)
    {
        var g=new Grid();
        foreach(double w in weights) g.ColumnDefinitions.Add(new ColumnDefinition {Width=w==0?GridLength.Auto:new GridLength(w,GridUnitType.Star)});
        return g;
    }
    public static void Put(Grid g,UIElement item,int col){Grid.SetColumn(item,col);g.Children.Add(item);}
    public static StackPanel Row(){return new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};}
    public static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,FontFamily=Font,FontSize=14,FontWeight=FontWeights.SemiBold,Cursor=System.Windows.Input.Cursors.Hand,
            Padding=new Thickness(12,5,12,5),BorderThickness=new Thickness(0),Background=Brushes.Transparent,Foreground=B("#42534E"),
            Template=ButtonTemplate};
        b.Click+=delegate{action();}; return b;
    }
    static readonly ControlTemplate ButtonTemplate=(ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='box' CornerRadius='5' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='Opacity' Value='0.75'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='#598C88'/><Setter TargetName='box' Property='BorderThickness' Value='1'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
}

internal sealed class MonitorWindow : Window
{
    const string Ink="#162D29",Muted="#586F68",Line="#D6E0DA",Paper="#F2F5F1",Green="#19715C",Purple="#7955AF",Blue="#237CAA";
    readonly Telemetry data=new Telemetry();
    readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
    readonly List<Action<Sample>> cardUpdates=new List<Action<Sample>>();
    readonly List<Plot> minis=new List<Plot>();
    readonly TextBlock clock=StyleKit.Text("—",14,"#D2DFD7"),status=StyleKit.Text("Ожидание",14,"#BCE6B7",true);
    readonly TextBlock caption=StyleKit.Text("",14,Muted),owner=StyleKit.Text("",14,Muted),unit=StyleKit.Text("",13,Muted);
    readonly TextBlock interval=StyleKit.Text("",13,Muted);
    readonly Ellipse lamp=new Ellipse {Width=7,Height=7,Margin=new Thickness(0,0,7,0)};
    readonly Plot chart=new Plot {Height=191};
    readonly StackPanel overview=new StackPanel();
    readonly List<Plot> strips=new List<Plot>();
    readonly List<TextBlock[]> stripLabels=new List<TextBlock[]>();
    readonly TextBlock inspectTime=StyleKit.Text("",12,Muted);
    readonly StackPanel legend=StyleKit.Row();
    readonly List<Button> views=new List<Button>(),modes=new List<Button>(),ranges=new List<Button>();
    readonly HashSet<K> hidden=new HashSet<K>();
    static readonly Trace[][] Groups={
        new[]{new Trace(K.Cpu,"CPU",Green),new Trace(K.Gpu,"GPU",Purple),new Trace(K.Water,"Жидкость",Blue)},
        new[]{new Trace(K.CpuLoad,"CPU",Green),new Trace(K.GpuLoad,"GPU",Purple)},
        new[]{new Trace(K.Top,"Радиатор",Green),new Trace(K.Bottom,"Вдув",Purple),new Trace(K.Pump,"Помпа",Blue),new Trace(K.Small,"M.2","#AC7332"),new Trace(K.GpuFan,"GPU","#9B5370")},
        new[]{new Trace(K.Noise,"Микрофон","#A46939")}
    };
    static readonly int[] Durations={1,5,15,60,360,1440};
    int group=-1,minutes=15;
    bool archive,busy,closed;
    Frame frame=new Frame();
    internal MonitorWindow()
    {
        Title="CaseCooling · Монитор охлаждения"; FontFamily=StyleKit.Font; Background=StyleKit.B(Paper);
        Width=Math.Min(1210,SystemParameters.WorkArea.Width); Height=Math.Min(875,SystemParameters.WorkArea.Height);
        MinWidth=790; MinHeight=520; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var shell=new DockPanel(); Content=shell;
        var top=Header(); DockPanel.SetDock(top,Dock.Top);shell.Children.Add(top);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        shell.Children.Add(scroll);
        var page=new StackPanel{Margin=new Thickness(27,20,27,20),MaxWidth=1650}; scroll.Content=page;
        var title=StyleKit.Columns(1,0);page.Children.Add(title);
        var names=new StackPanel(); names.Children.Add(StyleKit.Text("Монитор охлаждения",33,Ink,true)); names.Children.Add(caption);
        StyleKit.Put(title,names,0);
        var switcher=StyleKit.Row();
        foreach(string s in new[]{"Сейчас","Тест CPU + GPU"})
        {
            bool test=views.Count==1;
            var b=StyleKit.Button(s,delegate {ChangeView(test);});b.IsEnabled=!test || data.ArchivePath!=null;
            views.Add(b);switcher.Children.Add(b);
        }
        var switchBox=StyleKit.Box(switcher,"#E1E8E2",7,new Thickness(3));switchBox.VerticalAlignment=VerticalAlignment.Center;
        StyleKit.Put(title,switchBox,1);
        var metrics=StyleKit.Columns(1,1,1);metrics.Margin=new Thickness(0,18,0,17);page.Children.Add(metrics);
        Metric(metrics,0,"Процессор","Ryzen 9 3950X",Groups[0][0],"#E0EEE5","\uE950",K.CpuLoad);
        Metric(metrics,1,"Видеокарта","Radeon RX 480",Groups[0][1],"#EDE7F5","\uE7F4",K.GpuLoad);
        Metric(metrics,2,"Жидкость","NZXT Kraken X",Groups[0][2],"#DFEDF4","\uE9CA",K.Pump);
        var middle=StyleKit.Columns(1.86,1);page.Children.Add(middle);
        var history=History();history.Margin=new Thickness(0,0,8,0);StyleKit.Put(middle,history,0);
        var speeds=Speeds();speeds.Margin=new Thickness(8,0,0,0);StyleKit.Put(middle,speeds,1);
        var lower=Details();lower.Margin=new Thickness(0,16,0,0);page.Children.Add(lower);
        var foot=StyleKit.Columns(1,0);foot.Margin=new Thickness(2,12,2,0);StyleKit.Put(foot,owner,0);StyleKit.Put(foot,interval,1);page.Children.Add(foot);
        UpdateButtons();BuildLegend();
        timer.Tick+=async delegate{await Refresh();}; Loaded+=async delegate{timer.Start();await Refresh();};
        Closed+=delegate{closed=true;timer.Stop();};
    }
    Border Header()
    {
        var g=StyleKit.Columns(0,1,0);
        var brand=StyleKit.Row();
        var blade=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 15,1 C 22,0 26,4 26,9 C 22,11 19,12 16,13 Z M 28,14 C 31,21 28,26 23,28 C 20,25 18,22 17,18 Z M 17,29 C 10,32 5,28 3,24 C 6,20 9,18 12,17 Z M 1,18 C -1,11 3,5 8,4 C 11,8 12,10 13,13 Z"),Fill=StyleKit.B("#B8E89B"),Width=29,Height=29,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,10,0)};
        brand.Children.Add(blade);brand.Children.Add(StyleKit.Text("CaseCooling",24,"#F5FAF5",true));StyleKit.Put(g,brand,0);
        var model=StyleKit.Text("X570 I AORUS PRO WIFI",13,"#A3B8AF"); model.Margin=new Thickness(23,0,0,0);StyleKit.Put(g,model,1);
        var right=StyleKit.Row();right.Children.Add(lamp);right.Children.Add(status);clock.Margin=new Thickness(17,0,0,0);right.Children.Add(clock);StyleKit.Put(g,right,2);
        return StyleKit.Box(g,"#172E28",0,new Thickness(28,10,28,11));
    }
    void Metric(Grid g,int col,string label,string model,Trace trace,string tint,string glyph,K second)
    {
        var p=new StackPanel();var head=StyleKit.Columns(1,0);p.Children.Add(head);
        var name=StyleKit.Text(label,18,Ink,true);StyleKit.Put(head,name,0);
        var icon=StyleKit.Text(glyph,16,"#465B53");icon.FontFamily=new FontFamily("Segoe MDL2 Assets");StyleKit.Put(head,icon,1);
        p.Children.Add(StyleKit.Text(model,13,Muted));
        var body=StyleKit.Columns(0,1);body.Margin=new Thickness(0,5,0,2);p.Children.Add(body);
        var number=StyleKit.Row();var value=StyleKit.Text("—",46,Ink,true);
        number.Children.Add(value);var c=StyleKit.Text("°C",20,Muted);c.Margin=new Thickness(4,6,0,0);number.Children.Add(c);StyleKit.Put(body,number,0);
        var spark=new Plot{Mini=true,Height=31,Margin=new Thickness(12,3,0,0),Series=new[]{trace},Minutes=5};
        minis.Add(spark);StyleKit.Put(body,spark,1);
        var sub=StyleKit.Text("",14,Ink);p.Children.Add(sub);
        cardUpdates.Add(s=>{value.Text=Format(s,trace.Key,1);sub.Text=second==K.Pump?"Помпа   "+Format(s,second,0)+" об/мин":"Загрузка   "+Format(s,second,0)+" %";});
        var card=StyleKit.Box(p,tint,11,new Thickness(17,11,17,13));card.Margin=new Thickness(col==0?0:6,0,col==2?0:6,0);StyleKit.Put(g,card,col);
    }
    Border History()
    {
        var p=new StackPanel();var h=StyleKit.Columns(1,0);p.Children.Add(h);StyleKit.Put(h,StyleKit.Text("Динамика",23,Ink,true),0);
        var row=StyleKit.Row();
        foreach(int duration in Durations)
        {
            int captured=duration;var b=StyleKit.Button(duration<60?duration+" мин":duration/60+" ч",delegate{minutes=captured;Render();});b.Padding=new Thickness(6,3,6,3);
            ranges.Add(b);row.Children.Add(b);
        }
        StyleKit.Put(h,StyleKit.Box(row,Paper,6,new Thickness(2)),1);
        var tabs=StyleKit.Row();tabs.Margin=new Thickness(0,12,0,5);p.Children.Add(tabs);
        foreach(string label in new[]{"Обзор","Температура","Загрузка","Обороты","Шум"})
        {
            int index=modes.Count-1;var b=StyleKit.Button(label,delegate{group=index;BuildLegend();Render();});b.Padding=new Thickness(9,4,9,4);modes.Add(b);tabs.Children.Add(b);
        }
        p.Children.Add(overview);
        for(int i=0;i<3;i++){
            var tr=i==2?Groups[2].Take(2).ToArray():Groups[i];
            var line=StyleKit.Columns(1,0);var label=StyleKit.Text(new[]{"Температура · °C","Загрузка · %","Обороты · об/мин"}[i],13,Muted,true);StyleKit.Put(line,label,0);
            var keys=StyleKit.Row();var labels=new TextBlock[tr.Length];
            for(int j=0;j<tr.Length;j++){labels[j]=StyleKit.Text(tr[j].Label,13,Ink,true);labels[j].Foreground=tr[j].Color;labels[j].Margin=new Thickness(9,0,0,0);keys.Children.Add(labels[j]);}
            stripLabels.Add(labels);StyleKit.Put(line,keys,1);overview.Children.Add(line);
            var strip=new Plot{Series=tr,Compact=true,Height=i==2?70:46,Unit=new[]{"°C","%","об/мин"}[i],TimeLabels=i==2,Margin=new Thickness(0,1,0,5)};
            strips.Add(strip);overview.Children.Add(strip);strip.Inspect+=ShowInstant;
        }
        overview.Children.Add(inspectTime);
        unit.Margin=new Thickness(2,3,0,0);p.Children.Add(unit);p.Children.Add(chart);p.Children.Add(legend);
        return StyleKit.Box(p,"#FFFFFF",11,new Thickness(17,13,17,12),Line);
    }
    Border Speeds()
    {
        var p=new StackPanel();p.Children.Add(StyleKit.Text("Контуры охлаждения",22,Ink,true));
        var titles=StyleKit.Columns(1,0);titles.Margin=new Thickness(0,3,0,2);
        StyleKit.Put(titles,StyleKit.Text("Канал",13,Muted),0); StyleKit.Put(titles,StyleKit.Text("PWM   /   об/мин",13,Muted),1); p.Children.Add(titles);
        string[] labels={"Радиатор","Нижний вдув","Помпа Kraken","Вентилятор M.2","Видеокарта"};
        K[] rpm={K.Top,K.Bottom,K.Pump,K.Small,K.GpuFan}, duty={K.TopDuty,K.BottomDuty,K.PumpDuty,K.SmallDuty,K.GpuDuty};
        for(int i=0;i<labels.Length;i++)
        {
            K speed=rpm[i],control=duty[i];
            var strip=new StackPanel{Margin=new Thickness(0,9,0,2)};
            var g=StyleKit.Columns(1,0,0);strip.Children.Add(g);StyleKit.Put(g,StyleKit.Text(labels[i],15,Ink,true),0);
            var pwm=StyleKit.Text("—",13,Muted);pwm.Margin=new Thickness(0,0,10,0);StyleKit.Put(g,pwm,1);
            var actual=StyleKit.Text("—",16,Ink,true);actual.MinWidth=41;actual.TextAlignment=TextAlignment.Right;StyleKit.Put(g,actual,2);
            var bar=new Grid{Height=3,Margin=new Thickness(0,5,0,0),Background=StyleKit.B("#E6ECE8")};
            var width=new ScaleTransform(0,1);bar.Children.Add(new Border {Background=StyleKit.B(i==2?"#6CA1BA":i==4?"#A18DBB":"#72A28C"),RenderTransform=width});
            strip.Children.Add(bar);p.Children.Add(strip);
            cardUpdates.Add(s=>{pwm.Text=Format(s,control,0)+" %";actual.Text=Format(s,speed,0);double v=s==null?double.NaN:s[control];width.ScaleX=double.IsNaN(v)?0:Math.Min(1,Math.Max(0,v/100));});
        }
        return StyleKit.Box(p,"#FFFFFF",11,new Thickness(17,13,17,12),Line);
    }
    Border Details()
    {
        var p=new StackPanel();p.Children.Add(StyleKit.Text("Дополнительные датчики",18,Ink,true));
        var g=StyleKit.Columns(1,1,1.17,1.25,1.1,1.1);g.Margin=new Thickness(0,9,0,0);p.Children.Add(g);
        string[] labels={"CPU · CCD 1","CPU · CCD 2","SSD · MP510","SSD · Intel 512 ГБ","Мощность CPU","Шум · StreamCam"};
        K[] keys={K.Ccd1,K.Ccd2,K.Disk0,K.Disk1,K.Power,K.Noise};
        for(int i=0;i<keys.Length;i++)
        {
            K key=keys[i];string suffix=i<4?" °C":i==4?" Вт":" dBFS";
            var stack=new StackPanel();stack.Children.Add(StyleKit.Text(labels[i],13,Muted));
            var v=StyleKit.Text("—",19,Ink,true);stack.Children.Add(v);cardUpdates.Add(s=>v.Text=Format(s,key,key==K.Noise?1:0)+suffix);
            var edge=new Border{Child=stack,Padding=new Thickness(i==0?0:13,0,0,0),BorderThickness=new Thickness(i==0?0:1,0,0,0),BorderBrush=StyleKit.B(Line)};StyleKit.Put(g,edge,i);
        }
        return StyleKit.Box(p,"#FAFCF9",11,new Thickness(17,11,17,12),Line);
    }
    static string Format(Sample s,K key,int d){return StyleKit.Value(s==null?double.NaN:s[key],d);}
    string WaterTrend(Sample latest)
    {
        var before=frame.Rows.LastOrDefault(r=>r.Time<=latest.Time.AddMinutes(-2));
        if(before==null || (latest.Time-before.Time).TotalSeconds>130 || double.IsNaN(before[K.Water]+latest[K.Water]))
            return "Жидкость Kraken  ·  "+Format(latest,K.Water,1)+" °C";
        double delta=latest[K.Water]-before[K.Water];
        return Math.Abs(delta)<0.15 ? "Температура жидкости стабильна последние 2 мин" :
            "Жидкость "+(delta>0?"нагрелась":"остыла")+" на "+StyleKit.Value(Math.Abs(delta),1)+" °C за 2 мин";
    }
    void BuildLegend()
    {
        legend.Children.Clear();
        if(group<0)return;
        foreach(var t in Groups[group])
        {
            var tr=t;var b=StyleKit.Button("●  "+tr.Label,delegate{
                if(!hidden.Add(tr.Key))hidden.Remove(tr.Key);BuildLegend();Render();});
            b.Padding=new Thickness(4,2,5,2);b.FontSize=13;b.Foreground=hidden.Contains(tr.Key)?StyleKit.B("#89958E"):tr.Color;
            b.ToolTip="Показать или скрыть ряд «"+tr.Label+"»";legend.Children.Add(b);
        }
    }
    void Highlight(List<Button> list,int selected,string fill,string active)
    {
        for(int i=0;i<list.Count;i++)
        {
            list[i].Background=i==selected?StyleKit.B(fill):Brushes.Transparent;
            list[i].Foreground=StyleKit.B(i==selected?active:Muted);
        }
    }
    void UpdateButtons()
    {
        Highlight(views,archive?1:0,"#FFFFFF",Ink);Highlight(ranges,Array.IndexOf(Durations,minutes),"#DCE8DF",Ink);Highlight(modes,group+1,"#DFEEE4",Green);
    }
    async void ChangeView(bool test)
    {
        if(archive==test) return;
        archive=test;minutes=test?5:15;frame=new Frame{End=DateTime.UtcNow,Archive=test};Render();await Refresh();
    }
    async Task Refresh()
    {
        if(busy || closed) return;
        busy=true;bool requested=archive;
        try
        {
            var next=await Task.Run(()=>data.ReadFrame(requested));
            if(!closed && requested==archive){frame=next;Render();}
        }
        catch(Exception ex)
        {
            frame=new Frame{End=DateTime.UtcNow,Archive=requested,Source="Журнал недоступен"};Render();caption.ToolTip=ex.Message;
        }
        finally{busy=false;}
        if(!closed && requested!=archive)await Refresh();
    }
    void Render()
    {
        UpdateButtons();
        var latest=frame.Rows.LastOrDefault();
        bool active=archive || frame.Fresh;
        string reason=frame.DirectReason;
        if(!frame.PerSecond && latest!=null)reason=latest.Reason;
        bool guard=frame.Owner=="CaseCooling" && new[]{"sensor-or-tach","temperature","watchdog","startup"}.Contains(reason);
        Sample displayed=latest;
        bool delayed=false;
        if(!archive && frame.PerSecond && frame.Fresh && latest!=null){
            displayed=new Sample{Time=latest.Time,Values=(double[])latest.Values.Clone()};
            for(int j=frame.Rows.Count-2;j>=0 && (latest.Time-frame.Rows[j].Time).TotalSeconds<=3;j--){
                var prev=frame.Rows[j];
                for(int k=0;k<(int)K.Count;k++)if(double.IsNaN(displayed.Values[k]) && !double.IsNaN(prev.Values[k])){
                    displayed.Values[k]=prev.Values[k];delayed=true;
                }
            }
        }
        bool partial=displayed!=null && new[]{K.Cpu,K.Gpu,K.Water,K.Top,K.Bottom,K.Pump}.Any(k=>double.IsNaN(displayed[k]));
        status.Text=archive?"Запись теста":!frame.Fresh?"Нет свежих данных":guard?"Защитный режим":partial?"Часть датчиков недоступна":delayed?"Задержка датчика до 3 с":"В реальном времени";
        lamp.Fill=StyleKit.B(archive?"#9DCADF":!frame.Fresh||guard||partial?"#F0BD7B":"#ACE08F");
        clock.Text=latest==null?"—":latest.Time.ToLocalTime().ToString("HH:mm:ss");
        caption.Text=archive?(latest==null?"Загрузка записи…":"Показания в конце прогона  ·  "+latest.Time.ToLocalTime().ToString("dd.MM.yyyy")):
            latest==null?"Ожидание показаний службы или логгера":frame.Fresh?WaterTrend(latest):
            "Последняя запись  "+latest.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        owner.Text=(archive?"Управление во время теста":"Управление")+"  ·  "+(frame.Owner??"—");
        interval.Text=(frame.Source??"")+"   ·   "+(archive?"Архив":frame.PerSecond?"Шаг 1 с":"Журнал измерений");
        foreach(var update in cardUpdates)update(active?displayed:null);
        foreach(var mini in minis){mini.Rows=frame.Rows;mini.End=frame.End;mini.RefreshPlot();}
        bool stacked=group==-1;
        overview.Visibility=stacked?Visibility.Visible:Visibility.Collapsed;
        chart.Visibility=legend.Visibility=unit.Visibility=stacked?Visibility.Collapsed:Visibility.Visible;
        if(stacked){
            foreach(var s in strips){s.Rows=frame.Rows;s.End=frame.End;s.Minutes=minutes;s.RefreshPlot();}
            ShowInstant(strips[0].SharedTime);
        }else{
            chart.Rows=frame.Rows;chart.End=frame.End;chart.Minutes=minutes;
            chart.Series=Groups[group].Where(t=>!hidden.Contains(t.Key)).ToArray();
            chart.Unit=new[]{"°C","%","об/мин","dBFS"}[group];unit.Text=chart.Unit; chart.RefreshPlot();
        }
    }
    void ShowInstant(DateTime? at)
    {
        Sample sample=archive||frame.Fresh?frame.Rows.LastOrDefault():null;
        if(at.HasValue){
            int i=Plot.Lower(frame.Rows,at.Value);
            if(i==frame.Rows.Count || (i>0 && (at.Value-frame.Rows[i-1].Time).Duration()<(frame.Rows[i].Time-at.Value).Duration()))--i;
            sample=i>=0 && (frame.Rows[i].Time-at.Value).Duration().TotalSeconds<=Math.Max(3,minutes*0.12)?frame.Rows[i]:null;
        }
        for(int i=0;i<strips.Count;i++){
            var s=strips[i]; s.SharedTime=at;s.InvalidateVisual();
            for(int j=0;j<s.Series.Length;j++)stripLabels[i][j].Text=s.Series[j].Label+" "+Format(sample,s.Series[j].Key,i==0?1:0);
        }
        inspectTime.Text=at.HasValue?at.Value.ToLocalTime().ToString("dd.MM   HH:mm:ss"):"";
    }
}

internal static class DashboardApp
{
    [STAThread] public static void Main()
    {
        var app=new Application();app.Run(new MonitorWindow());
    }
}
