using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;

// Tail by byte offset. An incomplete final line is read on the next poll.
internal sealed class Journal
{
    const int MaxRead=2*1024*1024;
    readonly bool service;
    public readonly List<Sample> Rows=new List<Sample>();
    long cursor;
    DateTime created;
    int[] columns;
    int reason, board, krakenAge, microphone;
    static readonly string[] Keys={"cpu_c","gpu_c","coolant_c","cpu_load_pct","gpu_load_pct","cpu_package_w",
        "radiator_rpm","intake_rpm","m2_rpm","pump_rpm","gpu_rpm","radiator_pct","intake_pct","m2_pct","pump_pct",
        "gpu_fan_pct","ccd1_c","ccd2_c","disk0_c","disk1_c","mic_rms_dbfs"};
    const string ServiceHeader="utc,cpu_c,ccd1_c,ccd2_c,gpu_c,coolant_c,pump_rpm,radiator_rpm,intake_rpm,m2_rpm,disk0_c,disk1_c,rad_pwm,air_pwm,m2_pwm,rad_request,air_request,m2_request,reason,cpu_load_pct,gpu_load_pct,cpu_package_w,gpu_rpm,gpu_fan_pct,pump_pct";
    internal Journal(bool isService) { service=isService; }
    void Header(string header)
    {
        var names=header.TrimStart('\ufeff').Trim().Split(',');
        columns=Keys.Select(x=>Array.IndexOf(names,x)).ToArray();
        if (service)
        {
            columns[(int)K.TopDuty]=Array.IndexOf(names,"rad_pwm");
            columns[(int)K.BottomDuty]=Array.IndexOf(names,"air_pwm");
            columns[(int)K.SmallDuty]=Array.IndexOf(names,"m2_pwm");
        }
        reason=Array.IndexOf(names,"reason"); board=Array.IndexOf(names,"board_status");
        krakenAge=Array.IndexOf(names,"kraken_age_s"); microphone=Array.IndexOf(names,"mic_status");
    }
    static string[] Fields(string s)
    {
        if (!s.Contains("\"")) return s.Split(',');
        var result=new List<string>(); var field=new StringBuilder(); bool quoted=false;
        for (int i=0;i<s.Length;i++)
        {
            char c=s[i];
            if (c=='"')
            {
                if (quoted && i+1<s.Length && s[i+1]=='"') {field.Append(c); i++;}
                else quoted=!quoted;
            }
            else if (!quoted && c==',') { result.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        result.Add(field.ToString()); return result.ToArray();
    }
    static string At(string[] fields,int i) { return i>=0 && i<fields.Length ? fields[i] : ""; }
    void Parse(string line)
    {
        if (line.StartsWith("utc,") || line.StartsWith("time_local,")) { Header(line); return; }
        if (line.Length<20 || line[0]!='2' || columns==null) return;
        var fields=Fields(line); DateTimeOffset time;
        if (!DateTimeOffset.TryParse(fields[0],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out time)) return;
        var r=new Sample {Time=time.UtcDateTime,Reason=At(fields,reason)};
        for (int i=0;i<columns.Length;i++)
        {
            double v;
            if (double.TryParse(At(fields,columns[i]),NumberStyles.Float,CultureInfo.InvariantCulture,out v) && !double.IsInfinity(v))
                r.Values[i]=v;
        }
        if (board>=0 && At(fields,board)!="ok")
            foreach(var k in new[]{K.Cpu,K.Ccd1,K.Ccd2,K.Top,K.Bottom,K.Small,K.TopDuty,K.BottomDuty,K.SmallDuty}) r.Values[(int)k]=double.NaN;
        double age;
        if (r[K.Water]<=0 || (double.TryParse(At(fields,krakenAge),NumberStyles.Float,CultureInfo.InvariantCulture,out age) && age>12))
            foreach(var k in new[]{K.Water,K.Pump,K.PumpDuty}) r.Values[(int)k]=double.NaN;
        if (microphone>=0 && At(fields,microphone)!="ok") r.Values[(int)K.Noise]=double.NaN;
        Rows.Add(r);
    }
    internal void Update(string path)
    {
        var info=new FileInfo(path);
        if (!info.Exists) return;
        using (var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            long len=f.Length;
            if (columns==null || info.CreationTimeUtc!=created || cursor>len || len-cursor>MaxRead)
            {
                Rows.Clear(); created=info.CreationTimeUtc;
                using (var reader=new StreamReader(f,Encoding.UTF8,true,4096,true))
                {
                    string first=reader.ReadLine();
                    if (first==null) {columns=null;cursor=0;return;}
                    Header(service ? ServiceHeader : first);
                }
                cursor=Math.Max(0,len-MaxRead);
                if (cursor>0) { f.Position=cursor; while(f.Position<len && f.ReadByte()!=10) {} cursor=f.Position; }
            }
            if (cursor==len) return;
            f.Position=cursor; byte[] bytes=new byte[(int)(len-cursor)];
            int n=0;
            while(n<bytes.Length) { int got=f.Read(bytes,n,bytes.Length-n); if(got==0) break; n+=got; }
            int begin=0;
            for (int i=0;i<n;i++) if (bytes[i]==10)
            {
                int end=(i>begin && bytes[i-1]==13)?i-1:i;
                Parse(Encoding.UTF8.GetString(bytes,begin,end-begin).TrimStart('\ufeff')); begin=i+1;
            }
            cursor+=begin;
            if (Rows.Count>0)
            {
                var cutoff=Rows[Rows.Count-1].Time.AddMinutes(-61);
                Rows.RemoveAll(x=>x.Time<cutoff);
            }
        }
    }
    internal IEnumerable<Sample> Import(string path)
    {
        using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var reader=new StreamReader(f)){
            string first=reader.ReadLine();if(first==null)yield break;Header(first);
            string line;while((line=reader.ReadLine())!=null){Parse(line);if(Rows.Count>0){var row=Rows[0];Rows.Clear();yield return row;}}
        }
    }
}

internal sealed class Frame
{
    public List<Sample> Rows=new List<Sample>();
    public string Source,Owner;
    public string DirectReason;
    public bool PerSecond;
    public bool Archive,Fresh;
    public DateTime End;
}

internal sealed class Telemetry
{
    readonly string root;
    readonly Dictionary<string,Journal> cache=new Dictionary<string,Journal>(StringComparer.OrdinalIgnoreCase);
    readonly RingReader history=new RingReader();
    string owner;
    DateTime ownerChecked;
    public readonly string ArchivePath;
    internal Telemetry()
    {
        string bin=AppDomain.CurrentDomain.BaseDirectory;
        string pointer=Path.Combine(bin,"dashboard-data.txt");
        root=File.Exists(pointer)?File.ReadAllText(pointer).Trim():Directory.GetParent(bin.TrimEnd('\\')).FullName;
        if (Directory.Exists(root))
            ArchivePath=Directory.GetDirectories(root,"combined-*").OrderByDescending(x=>x)
                .FirstOrDefault(x=>File.Exists(Path.Combine(x,"load.csv")));
    }
    List<Sample> Read(string path,bool service)
    {
        Journal journal;
        if(!cache.TryGetValue(path,out journal)) cache[path]=journal=new Journal(service);
        try { journal.Update(path); } catch(IOException) {} catch(UnauthorizedAccessException) {}
        return journal.Rows;
    }
    static bool Active(string process)
    {
        var all=Process.GetProcessesByName(process);
        foreach(var p in all) p.Dispose();
        return all.Length>0;
    }
    static bool ServiceActive()
    {
        try { using(var sc=new ServiceController("CaseCooling")) return sc.Status==ServiceControllerStatus.Running; }
        catch(InvalidOperationException) {return false;}
    }
    static List<Sample> Combine(List<Sample> logger,List<Sample> direct)
    {
        if (logger.Count==0) return direct;
        if (direct.Count==0) return logger;
        var joined=new List<Sample>(); int j=0;
        foreach (var s in direct)
        {
            while(j<logger.Count && logger[j].Time<s.Time.AddSeconds(-1.25)) joined.Add(logger[j++]);
            int near=j;
            if (j>0 && (j==logger.Count || Math.Abs((logger[j-1].Time-s.Time).TotalSeconds)<Math.Abs((logger[j].Time-s.Time).TotalSeconds))) near=j-1;
            joined.Add(near<logger.Count && Math.Abs((logger[near].Time-s.Time).TotalSeconds)<=3 ? s.WithExtras(logger[near]) : s);
            while(j<logger.Count && logger[j].Time<=s.Time.AddSeconds(1.25)) ++j;
        }
        joined.AddRange(logger.Skip(j)); return joined.OrderBy(x=>x.Time).ToList();
    }
    public Frame ReadFrame(bool archive)
    {
        var f=new Frame { Archive=archive, End=DateTime.UtcNow };
        if(!archive && File.Exists(Ring.Installed))
        {
            f.Rows=history.Read(Ring.Installed,Ring.Stamp(f.End));
            if((f.End-ownerChecked).TotalSeconds>5 || owner==null){
                owner=ServiceActive()?"CaseCooling":Active("FanControl")?"Fan Control":"Не определено";
                ownerChecked=f.End;
            }
            f.Owner=owner;f.Source="Суточная история";f.PerSecond=true;
            if(owner=="CaseCooling"){
                var decisions=Read(Path.Combine(Path.GetDirectoryName(Ring.Installed),"service.log"),true);
                var d=decisions.LastOrDefault();
                if(d!=null && (f.End-d.Time).TotalSeconds<=12)f.DirectReason=d.Reason;
            }
            if(f.Rows.Count>0){
                var last=f.Rows[f.Rows.Count-1].Time; f.Fresh=(f.End-last).TotalSeconds<=4;
            }
            return f;
        }
        List<Sample> logger=new List<Sample>(),direct=new List<Sample>();
        if(archive && ArchivePath!=null)
        {
            logger.AddRange(Read(Path.Combine(ArchivePath,"load.csv"),false));
            direct.AddRange(Read(Path.Combine(ArchivePath,"service.log"),true));
            if(logger.Count>0)
                direct.RemoveAll(x=>x.Time<logger[0].Time || x.Time>logger[logger.Count-1].Time);
            f.Source="Запись CPU + GPU"; f.Owner="CaseCooling";
        }
        else if (!archive)
        {
            string sessions=Path.Combine(Path.GetDirectoryName(root),"cooling-logger","sessions");
            if(Directory.Exists(sessions))
                foreach(var dir in Directory.GetDirectories(sessions).OrderByDescending(x=>x).Take(2).Reverse())
                    logger.AddRange(Read(Path.Combine(dir,"metrics.csv"),false));
            string log=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"CaseCooling","service.log");
            direct.AddRange(Read(log+".1",true)); direct.AddRange(Read(log,true));
            bool running=ServiceActive();
            f.Owner=running?"CaseCooling":Active("FanControl")?"Fan Control":"Не определено";
            f.Source=running?"Журнал службы":"Логгер датчиков";
            if (!running && direct.Count>0 && logger.Count==0) f.Source="Журнал службы";
        }
        logger=logger.OrderBy(x=>x.Time).ToList(); direct=direct.OrderBy(x=>x.Time).ToList();
        f.Rows=Combine(logger,direct);
        if(f.Rows.Count>0)
        {
            DateTime last=f.Rows[f.Rows.Count-1].Time;
            f.Fresh=(DateTime.UtcNow-last).TotalSeconds<=12 && last<=DateTime.UtcNow.AddSeconds(2);
            if(archive || !f.Fresh) f.End=last;
            f.Rows.RemoveAll(s=>s.Time<f.End.AddMinutes(-61));
        }
        return f;
    }
}
