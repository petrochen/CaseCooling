using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using LibreHardwareMonitor.Hardware;

internal static class Controller
{
    internal const int Conflict=20;
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static readonly ManualResetEvent Stop = new ManualResetEvent(false);
    internal const string Header="utc,cpu_c,ccd1_c,ccd2_c,gpu_c,coolant_c,pump_rpm,radiator_rpm,intake_rpm,m2_rpm,disk0_c,disk1_c,rad_pwm,air_pwm,m2_pwm,rad_request,air_request,m2_request,reason,cpu_load_pct,gpu_load_pct,cpu_package_w,gpu_rpm,gpu_fan_pct,pump_pct";
    static string Line(Board b, Reading r, Demand d)
    {
        double[] values = {r.Cpu,r.Ccd1,r.Ccd2,r.Gpu,r.Water,r.Pump,r.Radiator,r.Intake,r.M2,
            r.Disk0,r.Disk1,b.Duty(0),b.Duty(1),b.Duty(2),d.Radiator,d.Intake,d.M2};
        if (d.Reason=="probe") { values[14]=double.NaN; values[16]=double.NaN; }
        if (d.Reason=="release-observation" || d.Reason=="baseline")
            for (int i=14;i<=16;++i) values[i]=double.NaN;
        double[] stats={r.CpuLoad,r.GpuLoad,r.CpuPower,r.GpuFan,r.GpuDuty,r.PumpDuty};
        return DateTime.UtcNow.ToString("o")+","+Numbers(values)+","+d.Reason+","+Numbers(stats);
    }
    static string Numbers(double[] values)
    { return string.Join(",",values.Select(v=>double.IsNaN(v)?"":v.ToString("0.##",En))); }
    static void Apply(IControl[] channels,Demand d)
    {
        int[] target={d.Radiator,d.Intake,d.M2};
        Exception first=null;
        for (int i=0;i<channels.Length;++i)
        {
            try { channels[i].SetSoftware(target[i]); }
            catch (Exception ex) { if (first==null) first=ex; }
        }
        if (first!=null) throw new InvalidOperationException("PWM write failed.",first);
    }
    static bool Matches(Board b,Demand d)
    {
        return Math.Abs(b.Duty(0)-d.Radiator)<=2 && Math.Abs(b.Duty(1)-d.Intake)<=2 && Math.Abs(b.Duty(2)-d.M2)<=2;
    }
    static void Park(Board b,IControl[] channels)
    {
        var full=Demand.Full("park");
        for (int i=0;i<5;++i)
        {
            Apply(channels,full);
            Thread.Sleep(250);
            b.Update();
            if (Matches(b,full)) return;
        }
        throw new InvalidOperationException("Full cooling not confirmed by PWM readback.");
    }
    static void SpinSmallFan(Board board,IControl channel)
    {
        channel.SetSoftware(35);
        for (int i=0;i<10;++i)
        {
            Thread.Sleep(500);
            board.Update();
            var check=new Reading(); board.Fill(check);
            if (check.M2>=800 && Math.Abs(board.Duty(2)-35)<=2) return;
        }
        throw new InvalidOperationException("Small fan did not start at its measured setting.");
    }
    static void ReleaseIntake(Board board,IControl channel,double duty,double rpm)
    {
        channel.SetDefault();
        for (int i=0;i<15;++i)
        {
            Thread.Sleep(850); board.Update();
            var r=new Reading(); board.Fill(r);
            Console.WriteLine(Line(board,r,new Demand(0,0,0,"release-observation")));
            bool dutyReturned=double.IsNaN(duty) ? double.IsNaN(board.Duty(1)) :
                Math.Abs(board.Duty(1)-duty)<=6;
            if (dutyReturned && Math.Abs(r.Intake-rpm)<=140) return;
        }
        throw new InvalidOperationException("Intake release not verified.");
    }
    // Called in a dedicated child. EOF also covers a lost supervisor during handshake/stop.
    internal static int Run(string mode,int count)
    {
        bool parkTest=mode=="verify-park", idleWorker=mode=="idle";
        bool observe=mode=="observe", emergency=mode=="emergency" || parkTest, probe=mode=="probe";
        if (!observe)
        {
            if (Console.ReadLine()!="ARM") return 2;
            new Thread(delegate() { Console.ReadLine(); Stop.Set(); }) { IsBackground=true }.Start();
            if (Board.OtherWriter()) return Conflict;
        }
        Board.CheckBoard();
        if (!emergency) Board.CheckRadeon();
        using (var exclusive=observe ? null : new Mutex(false,@"Global\CaseCooling-X570-IT8688E-writer"))
        {
            if (!observe)
            {
                bool acquired;
                try { acquired=exclusive.WaitOne(0); }
                catch (AbandonedMutexException) { acquired=true; }
                if (!acquired) return Conflict;
            }
            var board=new Board(!emergency);
            var channels=observe ? null : board.Controls();
            var policy=new Policy();
            var gpu=new Radeon();
            var kraken=new Kraken();
            var timer=Stopwatch.StartNew();
            bool touched=false, competing=false, probePassed=false, smallOwned=false;
            int badPwm=0, probeTarget=0, good=0;
            double baseline=0, baselineDuty=0;
            Demand last=null;
            Console.WriteLine(Header);
            try
            {
                if (!observe && !probe)
                {
                    touched=true;
                    Park(board,channels);
                }
                for (int n=0; !Stop.WaitOne(0) && (count<=0 || n<count); ++n)
                {
                    if (!observe && Board.OtherWriter()) { competing=true; return Conflict; }
                    board.Update();
                    var r=new Reading();
                    board.Fill(r);
                    Demand d;
                    if (emergency) d=Demand.Full("watchdog");
                    else
                    {
                        kraken.Read(r,!observe && !probe);
                        r.Gpu=gpu.Read();
                        gpu.ReadStatistics(r);
                        r.Disk0=Disk.Temperature(0); r.Disk1=Disk.Temperature(1);
                        d=policy.Evaluate(r,timer.Elapsed.TotalSeconds);
                    }
                    if (probe)
                    {
                        if (!touched && !smallOwned && r.M2>=0 && r.M2<400 && double.IsNaN(board.Duty(2))
                            && r.Cpu>0 && r.Cpu<78 && r.Gpu>0 && r.Gpu<65 && r.Water>=5 && r.Water<40
                            && r.Pump>=800 && r.Radiator>=350 && r.Intake>=330
                            && r.Disk0>0 && r.Disk0<62 && r.Disk1>0 && r.Disk1<62)
                        {
                            Console.WriteLine("TAKEOVER");
                            smallOwned=true;
                            SpinSmallFan(board,channels[2]);
                            board.Fill(r);
                            d=policy.Evaluate(r,timer.Elapsed.TotalSeconds);
                        }
                        if (!r.Healthy() || r.Cpu>=78 || r.Gpu>=65 || r.Water>=40 || d.Reason!="curves")
                        {
                            Console.WriteLine(Line(board,r,d));
                            throw new InvalidOperationException("Idle conditions unavailable.");
                        }
                        if (!touched)
                        {
                            baseline=r.Intake; baselineDuty=board.Duty(1);
                            if (baseline>1480 || baselineDuty>63) throw new InvalidOperationException("Intake already high.");
                            probeTarget=double.IsNaN(baselineDuty) ? 75 : (int)Math.Max(60,Math.Ceiling(baselineDuty+12));
                            Console.WriteLine("TAKEOVER");
                            Console.WriteLine(Line(board,r,new Demand(0,0,0,"baseline")));
                        }
                        touched=true;
                        channels[1].SetSoftware(probeTarget);
                        d=new Demand(0,probeTarget,0,"probe");
                        if (Math.Abs(board.Duty(1)-probeTarget)<=2 && r.Intake>baseline+100) good++;
                        else good=0;
                        probePassed=good>=3;
                    }
                    else if (!observe)
                    {
                        if (last!=null && !Matches(board,last)) ++badPwm; else badPwm=0;
                        if (badPwm>=4) throw new InvalidOperationException("PWM feedback mismatch.");
                        // Start at full duty before evaluating the first downward transition.
                        if (last==null) d=Demand.Full("startup");
                        touched=true;
                        Apply(channels,d);
                        if (idleWorker && (r.Cpu>=78 || r.Gpu>=65 || r.Water>=40 || r.Disk0>=62 || r.Disk1>=62))
                            throw new InvalidOperationException("Idle-only trial limit.");
                        if (idleWorker && last!=null && Matches(board,last) && last.Reason=="curves"
                            && d.Reason=="curves" && r.Healthy() && last.Radiator<85 && last.Intake<75)
                            Console.WriteLine("READY-FOR-FAULT");
                        if (parkTest && Matches(board,d) && r.Radiator>=1900 && r.Intake>=1600 && r.M2>=3500)
                            ++good;
                        else if (parkTest) good=0;
                        last=d;
                    }
                    Console.WriteLine(Line(board,r,d));
                    if (r.Healthy()) ++healthySamples;
                    if (probePassed || (parkTest && good>=3) || (count>0 && n+1>=count)) break;
                    Stop.WaitOne(1500);
                }
            }
            finally
            {
                if ((touched || smallOwned) && !competing && !Board.OtherWriter())
                {
                    if (probe)
                    {
                        try { if (touched) ReleaseIntake(board,channels[1],baselineDuty,baseline); }
                        finally { if (smallOwned) channels[2].SetDefault(); }
                    }
                    else Park(board,channels);
                }
            }
            if (observe && healthySamples==0) return 3;
            if (probe && !probePassed) return 4;
            if (parkTest && good<3) return 4;
            return 0;
        }
    }
    static int healthySamples;
}
