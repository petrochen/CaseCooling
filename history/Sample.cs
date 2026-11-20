using System;
using System.Linq;
internal enum K { Cpu,Gpu,Water,CpuLoad,GpuLoad,Power,Top,Bottom,Small,Pump,GpuFan,TopDuty,BottomDuty,SmallDuty,PumpDuty,GpuDuty,Ccd1,Ccd2,Disk0,Disk1,Noise,Count }

internal sealed class Sample
{
    public DateTime Time;
    public double[] Values=Enumerable.Repeat(double.NaN,(int)K.Count).ToArray();
    public string Reason;
    public double this[K k] { get { return Values[(int)k]; } }
    public Sample WithExtras(Sample other)
    {
        var copy=(Sample)MemberwiseClone(); copy.Values=(double[])Values.Clone();
        // The board worker owns all thermal readings, including missing ones.
        foreach (var k in new[]{K.CpuLoad,K.GpuLoad,K.Power,K.GpuFan,K.GpuDuty,K.PumpDuty,K.Noise})
            if (double.IsNaN(copy[k])) copy.Values[(int)k]=other[k];
        return copy;
    }
}

