using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;

internal static class Program
{
    internal static readonly string Exe=Assembly.GetExecutingAssembly().Location;
    static void Require(bool pass,string message)
    { if (!pass) throw new InvalidOperationException(message); }
    static void SelfTest()
    {
        var p=new Policy();
        var r=new Reading { Cpu=80,Gpu=66,Water=44.5,Pump=2300,Radiator=1710,Intake=1230,M2=3300 };
        var d=p.Evaluate(r,0);
        Require(d.Radiator==78 && d.Intake==55 && d.M2==35,"Mixed anchor");
        r.Water=45; Require(p.Evaluate(r,1).Radiator==88,"45 degree boost");
        r.Water=44.6; Require(p.Evaluate(r,3).Radiator==88,"Hysteresis");
        r.Water=44.4; p.Evaluate(r,4); Require(p.Evaluate(r,18).Radiator==78,"Release");
        r.Cpu=50; r.Gpu=75; Require(p.Evaluate(r,19).Intake==67,"GPU-only intake");
        r.Gpu=66; r.Ccd2=88; Require(p.Evaluate(r,20).Radiator==100,"CCD safeguard");
        r.Ccd2=60; r.Water=double.NaN; Require(p.Evaluate(r,21).Intake==100,"Missing USB");
        r.Water=40; r.Pump=0; Require(p.Evaluate(r,22).Radiator==100,"Pump stall");
        r.Pump=2300; r.Disk0=68; Require(p.Evaluate(r,23).M2==100,"SSD safeguard");
        int recoveries=0;
        var guard=new Supervisor(delegate(string line) {
            Console.WriteLine(line);
            if (line=="fixture-safe") Interlocked.Increment(ref recoveries);
        });
        Require(guard.Run(false,"hang")==5,"Frozen worker not terminated");
        Require(guard.Run(false,"crash")==9,"Worker exit status");
        Require(recoveries==2,"Expected automatic recovery after both failures");
        Console.WriteLine("PASS: curves, cooling safeguards, worker containment and deadlines (simulated).");
    }
    static bool Elevated()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static int Main(string[] args)
    {
        try
        {
            string mode=args.Length==0?"observe":args[0];
            if (mode=="self-test") { SelfTest(); return 0; }
            if (mode=="fixture-hang" || mode=="fixture-crash" || mode=="fixture-safe")
            {
                if (Console.ReadLine()!="ARM") return 2;
                Console.WriteLine(mode);
                if (mode=="fixture-hang") Thread.Sleep(Timeout.Infinite);
                return mode=="fixture-crash"?9:0;
            }
            if (mode=="observe")
            {
                int n=args.Length>1 ? int.Parse(args[1]) : 5;
                Require(n>=1 && n<=86400,"Expected 1..86400 readings.");
                if (args.Length>2)
                {
                    using (var writer=new StreamWriter(new FileStream(args[2],FileMode.CreateNew,FileAccess.Write,FileShare.Read)))
                    {
                        writer.AutoFlush=true;
                        Console.SetOut(writer);
                        return Controller.Run(mode,n);
                    }
                }
                return Controller.Run(mode,n);
            }
            if (mode=="intake-test" || mode=="idle-fault-test" || mode=="service" || mode.StartsWith("worker-"))
            {
                Require(Elevated(),"Administrator privileges required for direct PWM.");
                if (mode=="intake-test") return new Supervisor(Console.WriteLine).Run(true);
                if (mode=="idle-fault-test") return new Supervisor(Console.WriteLine).Run(false,null,true);
                if (mode=="service") { ServiceBase.Run(new CoolingService()); return 0; }
                switch (mode) {
                    case "worker-normal": return Controller.Run("normal",0);
                    case "worker-probe": return Controller.Run("probe",30);
                    case "worker-emergency": return Controller.Run("emergency",0);
                    case "worker-park": return Controller.Run("emergency",1);
                    case "worker-verify-park": return Controller.Run("verify-park",12);
                    case "worker-idle": return Controller.Run("idle",0);
                }
            }
            Console.Error.WriteLine("CaseCooling observe [samples [new.csv]] | self-test | intake-test | idle-fault-test | service");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
}
