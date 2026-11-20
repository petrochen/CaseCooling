using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Windows closes the job handle on service termination, releasing the old worker
// before SCM recovery can start a replacement. The child waits for ARM before writing.
internal sealed class WorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    [StructLayout(LayoutKind.Sequential)] struct Limits
    {
        public long ProcessTime,JobTime; public uint Flags; public UIntPtr MinSet,MaxSet;
        public uint Active; public UIntPtr Affinity; public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct Extended
    {
        public Limits Basic;
        public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;
        public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;
    }
    [DllImport("kernel32",SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32",SetLastError=true)] static extern bool SetInformationJobObject(WorkerJob job,int id,ref Extended info,uint length);
    [DllImport("kernel32",SetLastError=true)] static extern bool AssignProcessToJobObject(WorkerJob job,IntPtr process);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    public WorkerJob() : base(true)
    {
        SetHandle(CreateJobObject(IntPtr.Zero,null));
        var config=new Extended();
        config.Basic.Flags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (IsInvalid || !SetInformationJobObject(this,9,ref config,(uint)Marshal.SizeOf(config)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Attach(Process child)
    {
        if (!AssignProcessToJobObject(this,child.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override bool ReleaseHandle() { return CloseHandle(handle); }
}

internal sealed class Supervisor
{
    internal readonly ManualResetEvent Stop = new ManualResetEvent(false);
    readonly Action<string> log;
    internal Supervisor(Action<string> logLine) { log=logLine; }
    internal int Watch(string mode, bool simulation, out bool touched)
    {
        int timeout=simulation ? 1400 : 18000;
        long last=Stopwatch.GetTimestamp();
        int takeover=0;
        int ready=0;
        var elapsed=Stopwatch.StartNew();
        string arg=simulation ? "fixture-"+mode : "worker-"+mode;
        var info=new ProcessStartInfo(Program.Exe,arg) {
            WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory, UseShellExecute=false,
            RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true,CreateNoWindow=true};
        using (var job=new WorkerJob())
        using (var child=new Process { StartInfo=info })
        {
            child.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) {
                if (e.Data==null) return;
                Interlocked.Exchange(ref last,Stopwatch.GetTimestamp());
                if (e.Data=="TAKEOVER") Interlocked.Exchange(ref takeover,1);
                if (e.Data=="READY-FOR-FAULT") Interlocked.Increment(ref ready);
                log(e.Data);
            };
            child.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(e.Data!=null) log(e.Data); };
            child.Start();
            try
            {
                job.Attach(child);
                child.BeginOutputReadLine(); child.BeginErrorReadLine();
                child.StandardInput.WriteLine("ARM"); child.StandardInput.Flush();
                bool stopping=false;
                var stopTime=new Stopwatch();
                while (!child.WaitForExit(200))
                {
                    if (mode=="idle" && Volatile.Read(ref ready)>=3)
                    {
                        log("Injected worker termination after confirmed direct PWM.");
                        child.Kill(); child.WaitForExit();
                        touched=true;
                        return 7;
                    }
                    if (Stop.WaitOne(0) && !stopping)
                    {
                        stopping=true; stopTime.Start();
                        try { child.StandardInput.Close(); } catch(IOException) { }
                    }
                    double silence=(Stopwatch.GetTimestamp()-Interlocked.Read(ref last))*1000.0/Stopwatch.Frequency;
                    if (silence>timeout || stopTime.ElapsedMilliseconds>timeout ||
                        (mode=="idle" && elapsed.Elapsed.TotalSeconds>50))
                    {
                        log("Watchdog deadline exceeded: "+mode);
                        child.Kill(); child.WaitForExit();
                        touched=takeover!=0;
                        return 5;
                    }
                }
                child.WaitForExit(); // finish asynchronous reads
                touched=takeover!=0;
                return child.ExitCode;
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
            }
        }
    }
    internal int Run(bool probe, string simulatedFault=null,bool idleFault=false)
    {
        bool touched;
        bool simulated=simulatedFault!=null;
        int result=Watch(simulatedFault ?? (probe?"probe":idleFault?"idle":"normal"),simulated,out touched);
        if (result==Controller.Conflict || (result==0 && (probe || Stop.WaitOne(0)))) return result;
        if (probe && !touched) return result;
        log("Worker stopped. Starting independent full-duty board worker. Exit="+result);
        do
        {
            // Even if stopping, attempt one full-duty park after an abnormal exit.
            int safe=Watch(simulated?"safe":probe?"park":idleFault?"verify-park":"emergency",simulated,out touched);
            if (idleFault) return result==7 && safe==0 ? 0 : 6;
            if (simulated || probe) return safe==0 ? result : safe;
            if (safe==Controller.Conflict || (Stop.WaitOne(0) && safe==0)) return safe;
            if (Stop.WaitOne(2000)) return safe==0 ? result : safe;
        } while (true);
    }
}

internal sealed class CoolingService : ServiceBase
{
    readonly object logLock=new object();
    Supervisor supervisor;
    Thread thread;
    internal CoolingService() { ServiceName="CaseCooling"; CanShutdown=true; }
    void Log(string line)
    {
        lock (logLock)
        {
            // Keep two bounded logs in the administrator-owned installation directory.
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"service.log");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length>2*1024*1024)
                { File.Delete(path+".1"); File.Move(path,path+".1"); }
                File.AppendAllText(path,line+Environment.NewLine);
            }
            catch (IOException) { } // Log availability must not interrupt cooling.
            catch (UnauthorizedAccessException) { }
        }
    }
    protected override void OnStart(string[] args)
    {
        supervisor=new Supervisor(Log);
        thread=new Thread(delegate() {
            try
            {
                int status=supervisor.Run(false);
                if (!supervisor.Stop.WaitOne(0))
                {
                    if (status==Controller.Conflict) { Log("Another fan controller is active; yielding."); Stop(); }
                    else Environment.FailFast("Cooling supervisor exited: "+status);
                }
                else if (status!=0) Log("Cooling shutdown unverified: "+status);
            }
            catch (Exception ex) { Environment.FailFast("Cooling supervisor failed.",ex); }
        }) { IsBackground=true };
        thread.Start();
    }
    protected override void OnStop()
    {
        supervisor.Stop.Set();
        if (thread!=Thread.CurrentThread)
        {
            RequestAdditionalTime(50000);
            if (!thread.Join(49000)) Environment.FailFast("Cooling stop deadline exceeded.");
        }
    }
    protected override void OnShutdown() { OnStop(); }
}
