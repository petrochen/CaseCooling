using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using HidSharp;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

internal sealed class Board
{
    readonly Computer computer = new Computer();
    readonly Dictionary<string, ISensor> sensors = new Dictionary<string, ISensor>();
    IHardware section;
    const string Ite = "/lpc/it8688e/0/";
    static string[] Query(string type, string field)
    {
        using (var query = new ManagementObjectSearcher("SELECT " + field + " FROM " + type))
        using (var rows = query.Get())
            return rows.Cast<ManagementObject>().Select(x => Convert.ToString(x[field]).Trim()).ToArray();
    }
    public static void CheckBoard()
    {
        if (!Query("Win32_BaseBoard","Product").Contains("X570 I AORUS PRO WIFI") ||
            !Query("Win32_Processor","Name").Any(x => x.Contains("3950X")))
            throw new InvalidOperationException("Machine identity mismatch.");
    }
    public static void CheckRadeon()
    {
        var amd = Query("Win32_VideoController","Name").Where(x =>
            x.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (amd.Length != 1 || !amd[0].Contains("RX 480")) throw new InvalidOperationException("Expected one RX 480.");
    }
    public static bool OtherWriter()
    {
        foreach (string name in new[]{"FanControl","FanControl.Service","NZXT CAM","CAM"})
            foreach (var process in Process.GetProcessesByName(name)) { process.Dispose(); return true; }
        return false;
    }
    public Board(bool cpu,bool motherboard=true)
    {
        computer.IsMotherboardEnabled = motherboard;
        computer.IsCpuEnabled = cpu;
        computer.Open();
        Update();
    }
    void Scan(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors) sensors[s.Identifier.ToString()] = s;
        foreach (var child in hw.SubHardware) Scan(child);
    }
    public void Update()
    {
        sensors.Clear();
        foreach (var hw in computer.Hardware) Scan(hw);
    }
    public void Close(){computer.Close();}
    // The read-only history service samples the CPU and the ITE on separate clocks.
    // Cache their hardware objects so unrelated motherboard providers aren't polled.
    public void UpdateSection(string id)
    {
        if(section==null) section=computer.Hardware.SelectMany(Flatten).Single(x=>x.Identifier.ToString()==id);
        section.Update();
        sensors.Clear();foreach(var s in section.Sensors)sensors[s.Identifier.ToString()]=s;
    }
    static IEnumerable<IHardware> Flatten(IHardware hw)
    {
        yield return hw;
        foreach(var child in hw.SubHardware)foreach(var x in Flatten(child))yield return x;
    }
    double Value(string id)
    {
        ISensor sensor;
        return sensors.TryGetValue(id,out sensor) && sensor.Value.HasValue ? sensor.Value.Value : double.NaN;
    }
    public double Duty(int index) { return Value(Ite + "control/" + index); }
    public void Fill(Reading r)
    {
        r.Cpu = Value("/amdcpu/0/temperature/2");
        r.CpuLoad = Value("/amdcpu/0/load/0");
        r.CpuPower = Value("/amdcpu/0/power/0");
        r.Ccd1 = Value("/amdcpu/0/temperature/3");
        r.Ccd2 = Value("/amdcpu/0/temperature/4");
        r.Radiator = Value(Ite+"fan/0"); r.Intake = Value(Ite+"fan/1"); r.M2 = Value(Ite+"fan/2");
    }
    public IControl[] Controls()
    {
        var controls = new IControl[3];
        for (int i=0; i<3; ++i)
        {
            ISensor s;
            if (!sensors.TryGetValue(Ite+"control/"+i,out s) || s.Control == null)
                throw new InvalidOperationException("Missing IT8688E PWM " + i + ". Elevated PawnIO access required.");
            controls[i] = s.Control;
        }
        return controls;
    }
}

internal sealed class Kraken
{
    bool configured;
    DateTime previous = DateTime.MinValue;
    static byte[] Report(params byte[] prefix)
    {
        byte[] report = new byte[64];
        prefix.CopyTo(report,0);
        return report;
    }
    public void Read(Reading r, bool configure)
    {
        try
        {
            var device = DeviceList.Local.GetHidDevices(0x1e71,0x2007).Single();
            if (device.GetMaxInputReportLength()!=64 || device.GetMaxOutputReportLength()!=64)
                throw new IOException("Unexpected Kraken report size.");
            // A fresh HID handle avoids queued status reports from the previous sample.
            using (var stream = device.Open())
            {
                stream.ReadTimeout = 1400;
                stream.WriteTimeout = 1400;
                if (configure && (!configured || (DateTime.UtcNow-previous).TotalSeconds>10))
                {
                    stream.Write(Report(0x70,0x02,0x01,0xb8,0x01));
                    stream.Write(Report(0x70,0x01));
                    byte[] curve = Report(0x72,0x01,0,0);
                    for (int t=20;t<=59;++t)
                        curve[4+t-20] = (byte)Math.Max(55,Math.Min(100,55+5*(t-33)));
                    stream.Write(curve);
                    configured = true;
                }
                for (int i=0; i<4; ++i)
                {
                    var packet = stream.Read();
                    if (packet.Length!=64 || packet[0]!=0x75 || packet[1]!=2) continue;
                    if (packet[16]>9) throw new IOException("Invalid coolant reading.");
                    r.Water = packet[15]+packet[16]*0.1;
                    r.Pump = packet[17] | (packet[18]<<8);
                    r.PumpDuty = packet[19];
                    previous = DateTime.UtcNow;
                    return;
                }
                throw new IOException("Missing Kraken status packet.");
            }
        }
        catch (Exception) { configured = false; } // Fresh Reading keeps both channels unavailable.
    }
}

internal sealed class Radeon
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Allocate(int size);
    static readonly Allocate Alloc = Marshal.AllocHGlobal;
    const string Dll = "atiadlxx.dll";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ADL_Main_Control_Create(Allocate fn,int connected);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ADL_Adapter_NumberOfAdapters_Get(out int count);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ADL_Overdrive5_Temperature_Get(int index,int controller,[In,Out] int[] data);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ADL_Overdrive5_CurrentActivity_Get(int index,[In,Out] int[] data);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int ADL_Overdrive5_FanSpeed_Get(int index,int controller,[In,Out] int[] data);
    public void ReadStatistics(Reading r)
    {
        if (index<0) return;
        try
        {
            int[] activity=new int[10]; activity[0]=40;
            if (ADL_Overdrive5_CurrentActivity_Get(index,activity)==0 && activity[4]>=0 && activity[4]<=100)
                r.GpuLoad=activity[4];
            int[] rpm={16,2,0,0}, duty={16,1,0,0};
            if (ADL_Overdrive5_FanSpeed_Get(index,0,rpm)==0) r.GpuFan=rpm[2];
            if (ADL_Overdrive5_FanSpeed_Get(index,0,duty)==0) r.GpuDuty=duty[2];
        }
        catch (EntryPointNotFoundException) { } // Optional statistics on older AMD drivers.
    }
    int index = -1;
    bool initialized;
    public double Read()
    {
        if (!initialized) initialized = ADL_Main_Control_Create(Alloc,1)==0;
        if (!initialized) return double.NaN;
        if (index >= 0)
        {
            double value = Temperature(index);
            if (!double.IsNaN(value)) return value;
            index = -1;
        }
        int count;
        if (ADL_Adapter_NumberOfAdapters_Get(out count)==0)
            for (int i=0;i<count;++i)
            {
                double value = Temperature(i);
                if (!double.IsNaN(value)) { index=i; return value; }
            }
        return double.NaN;
    }
    static double Temperature(int i)
    {
        int[] t = {8,0};
        return ADL_Overdrive5_Temperature_Get(i,0,t)==0 && t[1]>0 ? t[1]/1000.0 : double.NaN;
    }
}

internal static class Disk
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll")]
    static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int size,
        [Out] byte[] output,int capacity,out int returned,IntPtr overlap);
    public static double Temperature(int index)
    {
        using (var file = CreateFile(@"\\.\PhysicalDrive"+index,0,3,IntPtr.Zero,3,0,IntPtr.Zero))
        {
            int length;
            byte[] query=new byte[12], data=new byte[4096];
            query[0]=52;
            if (!file.IsInvalid && DeviceIoControl(file,0x2d1400,query,12,data,data.Length,out length,IntPtr.Zero)
                && length>=40 && BitConverter.ToUInt16(data,12)>0 && BitConverter.ToUInt16(data,24)==0)
            {
                short temp=BitConverter.ToInt16(data,26);
                if (temp != short.MinValue) return temp;
            }
            return double.NaN;
        }
    }
}
