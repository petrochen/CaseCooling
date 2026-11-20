using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

internal sealed class Signal
{
    readonly K[] keys;
    readonly object sync=new object();
    double[] latest;
    long measured,consumed;
    public Signal(params K[] channels){keys=channels;}
    public void Publish(params double[] values)
    {lock(sync){latest=values;measured=Stopwatch.GetTimestamp();}}
    public void Take(Sample row)
    {
        lock(sync){
            if(latest==null || consumed==measured || Stopwatch.GetTimestamp()-measured>Stopwatch.Frequency*1.6)return;
            for(int i=0;i<keys.Length;i++)row.Values[(int)keys[i]]=latest[i];
            consumed=measured;
        }
    }
}

// Only for the single-socket Zen 2 3950X verified by Board.CheckBoard().
// These are the temperature and package-energy counters used by LHM Amd17Cpu.
internal sealed class RyzenCounters : IDisposable
{
    readonly Mutex bus;
    readonly LibreHardwareMonitor.PawnIo.AmdFamily17 module;
    uint previousEnergy; long previousTime;
    public RyzenCounters(){
        Board.CheckBoard();
        bus=Mutex.OpenExisting(@"Global\Access_PCI");
        try{module=new LibreHardwareMonitor.PawnIo.AmdFamily17();}catch{bus.Dispose();throw;}
    }
    static double Ccd(uint raw){
        double c=(raw&4095)*0.125-305;
        return c>0 && c<125?c:double.NaN;
    }
    public Reading Read(){
        var r=new Reading();bool locked;
        try{locked=bus.WaitOne(120);}catch(AbandonedMutexException){locked=true;}
        if(!locked)return r;
        try{
            uint raw=module.ReadSmn(0x59800);
            double t=(raw>>21)*0.125;
            if((raw&0x80000)!=0 || (raw&0x30000)==0x30000)t-=49;
            if(t>0 && t<125)r.Cpu=t;
            r.Ccd1=Ccd(module.ReadSmn(0x59954));r.Ccd2=Ccd(module.ReadSmn(0x59958));
        }finally{bus.ReleaseMutex();}
        ulong units,energy;
        if(module.ReadMsr(0xC0010299,out units) && module.ReadMsr(0xC001029B,out energy)){
            long time=Stopwatch.GetTimestamp();double dt=(time-previousTime)/(double)Stopwatch.Frequency;
            if(previousTime!=0 && dt>0 && dt<5){
                double watts=unchecked((uint)energy-previousEnergy)*Math.Pow(0.5,(int)(units>>8)&31)/dt;
                if(watts>=0 && watts<500)r.CpuPower=watts;
            }
            previousEnergy=(uint)energy;previousTime=time;
        }
        return r;
    }
    public void Dispose(){module.Close();bus.Dispose();}
}

internal sealed class Recorder : ServiceBase
{
    readonly ManualResetEvent stop=new ManualResetEvent(false);
    readonly Signal cpu=new Signal(K.Cpu,K.Ccd1,K.Ccd2,K.Power);
    readonly Signal board=new Signal(K.Top,K.Bottom,K.Small,K.TopDuty,K.BottomDuty,K.SmallDuty);
    readonly Signal gpu=new Signal(K.Gpu,K.GpuLoad,K.GpuFan,K.GpuDuty);
    readonly Signal kraken=new Signal(K.Water,K.Pump,K.PumpDuty);
    readonly Signal disks=new Signal(K.Disk0,K.Disk1);
    readonly object audio=new object();
    Process mic; double energy;long samples;bool clean=true;
    Thread writer;
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    ulong previousIdle,previousTotal;
    void CpuLoad(Sample s)
    {
        ulong idle,kernel,user;if(!GetSystemTimes(out idle,out kernel,out user))return;
        ulong total=kernel+user;
        if(previousTotal>0 && total>previousTotal && idle>=previousIdle)
            s.Values[(int)K.CpuLoad]=Math.Max(0,Math.Min(100,100.0*(1-(double)(idle-previousIdle)/(total-previousTotal))));
        previousTotal=total;previousIdle=idle;
    }
    internal Recorder(){ServiceName="CaseCoolingHistory";CanShutdown=true;}
    void Poll(Action read)
    {
        new Thread(delegate(){
            var clock=Stopwatch.StartNew();
            while(!stop.WaitOne(0)){
                long start=clock.ElapsedMilliseconds;
                try{read();}catch(Exception){if(stop.WaitOne(1500))break;}
                stop.WaitOne((int)Math.Max(30,850-(clock.ElapsedMilliseconds-start)));
            }
        }){IsBackground=true}.Start();
    }
    void Audio()
    {
        var en=CultureInfo.InvariantCulture;
        while(!stop.WaitOne(0))
        {
            var info=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"streamcam_raw.exe")){
                UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};
            try{
                using(var p=Process.Start(info)){
                    lock(audio){mic=p;if(stop.WaitOne(0)){p.Kill();return;}}
                    string line;bool muted=true;
                    while((line=p.StandardOutput.ReadLine())!=null){
                        string[] fields=line.Split(' ');
                        if(fields[0]=="format" && fields.Length==7)muted=fields[6]!="0";
                        if(fields[0]!="stats" || fields.Length!=7 || muted)continue;
                        lock(audio){
                            samples+=long.Parse(fields[1],en); energy+=double.Parse(fields[2],en);
                            clean &= fields[5]=="0" && fields[6]=="0";
                        }
                    }
                    lock(audio){mic=null;}
                }
            }catch(Exception){lock(audio){mic=null;}}
            stop.WaitOne(30000);
        }
    }
    protected override void OnStart(string[] args)
    {
        stop.Reset();Board b=null;RyzenCounters c=null;var amd=new Radeon();var water=new Kraken();
        Poll(delegate{
            if(c==null)c=new RyzenCounters();
            try{
                var r=c.Read();
                cpu.Publish(r.Cpu,r.Ccd1,r.Ccd2,r.CpuPower);
            }catch{c.Dispose();c=null;throw;}
        });
        Poll(delegate{
            if(b==null){Board.CheckBoard();b=new Board(false);}
            try{
                b.UpdateSection("/lpc/it8688e/0");var r=new Reading();b.Fill(r);
                board.Publish(r.Radiator,r.Intake,r.M2,b.Duty(0),b.Duty(1),b.Duty(2));
                if(double.IsNaN(r.Radiator))throw new IOException("Board not ready");
            }catch{if(b!=null)b.Close();b=null;throw;}
        });
        Poll(delegate{var r=new Reading();r.Gpu=amd.Read();amd.ReadStatistics(r);gpu.Publish(r.Gpu,r.GpuLoad,r.GpuFan,r.GpuDuty);});
        Poll(delegate{var r=new Reading();water.Read(r,false);kraken.Publish(r.Water,r.Pump,r.PumpDuty);});
        Poll(delegate{disks.Publish(Disk.Temperature(0),Disk.Temperature(1));});
        new Thread(Audio){IsBackground=true}.Start();
        writer=new Thread(delegate(){
            try{
                using(var store=new RingWriter(Ring.Installed,Ring.Stamp(DateTime.UtcNow))){
                    long last=0;
                    while(!stop.WaitOne(0)){
                        DateTime now=DateTime.UtcNow;long second=Ring.Stamp(now);
                        if(second!=last){
                            var s=new Sample();
                            CpuLoad(s);cpu.Take(s);board.Take(s);gpu.Take(s);kraken.Take(s);disks.Take(s);
                            lock(audio){
                                if(clean && samples>=24000 && energy>0)s.Values[(int)K.Noise]=10*Math.Log10(energy/samples);
                                samples=0;energy=0;clean=true;
                            }
                            store.Put(second,s);last=second;
                        }
                        var due=Ring.Epoch.AddSeconds(second+1).AddMilliseconds(10);
                        stop.WaitOne((int)Math.Max(1,Math.Min(1000,(due-DateTime.UtcNow).TotalMilliseconds)));
                    }
                }
            }catch(Exception e){Environment.FailFast("Cannot write CaseCooling history.",e);}
        }){IsBackground=true};writer.Start();
    }
    protected override void OnStop()
    {
        stop.Set();
        lock(audio){if(mic!=null)try{if(!mic.HasExited)mic.Kill();}catch(InvalidOperationException){}}
        if(writer!=null)writer.Join(4000);
    }
    protected override void OnShutdown(){OnStop();}
    public static int Main(string[] args)
    {
        if(args.Length==0){ServiceBase.Run(new Recorder());return 0;}
        // One-time import before first service start, preserving the original sample times.
        if(args.Length>=2 && args[0]=="seed" && !File.Exists(Ring.Installed)){
            long now=Ring.Stamp(DateTime.UtcNow);
            string temp=Ring.Installed+".seed-"+Guid.NewGuid().ToString("N");
            try{
                using(var store=new RingWriter(temp,now))
                    foreach(string file in args.Skip(1)){
                        var journal=new Journal(false);
                        foreach(var s in journal.Import(file)){
                            long stamp=Ring.Stamp(s.Time);
                            if(stamp>now-Ring.Seconds && stamp<=now)store.Put(stamp,s,true);
                        }
                    }
                File.Move(temp,Ring.Installed);
            }finally{if(File.Exists(temp))File.Delete(temp);}
            return 0;
        }
        return 2;
    }
}
