using System;

internal sealed class Reading
{
    public double Cpu, Ccd1, Ccd2, Gpu, Water, Pump, Radiator, Intake, M2, Disk0, Disk1;
    public double CpuLoad=double.NaN, GpuLoad=double.NaN, CpuPower=double.NaN;
    public double GpuFan=double.NaN, GpuDuty=double.NaN, PumpDuty=double.NaN;
    public Reading()
    {
        Cpu = Ccd1 = Ccd2 = Gpu = Water = Pump = Radiator = Intake = M2 = Disk0 = Disk1 = double.NaN;
    }
    public bool Healthy()
    {
        return Cpu > 0 && Cpu < 110 && Gpu > 0 && Gpu < 115 && Water >= 5 && Water < 65
            && Pump >= 800 && Radiator >= 350 && Intake >= 330 && M2 >= 400;
    }
}

internal sealed class Demand
{
    public readonly int Radiator, Intake, M2;
    public readonly string Reason;
    public Demand(int rad, int air, int small, string reason)
    { Radiator = rad; Intake = air; M2 = small; Reason = reason; }
    public static Demand Full(string reason) { return new Demand(100, 100, 100, reason); }
}

internal sealed class Policy
{
    static readonly double[,] Rad = {{33,30},{36,43},{38,60},{40,78},{44.9,78},{45,88},{46,88},{47,100}};
    static readonly double[,] CpuAir = {{50,30},{60,38},{65,45},{70,55},{83,55},{86,80},{90,100}};
    static readonly double[,] GpuAir = {{45,30},{55,43},{63,55},{70,55},{75,67},{80,85},{85,100}};
    bool boosted;
    int rad, air;
    double radAt, airAt;

    static int Curve(double x, double[,] points)
    {
        if (x <= points[0,0]) return (int)points[0,1];
        for (int i = 1; i < points.GetLength(0); ++i)
        {
            if (x > points[i,0]) continue;
            double part = (x - points[i-1,0]) / (points[i,0] - points[i-1,0]);
            return (int)Math.Round(points[i-1,1] + part * (points[i,1] - points[i-1,1]));
        }
        return (int)points[points.GetLength(0)-1,1];
    }
    // Increases are immediate. Only lower after 14 seconds continuously below the last command.
    static int HoldDown(int requested, ref int previous, ref double since, double now)
    {
        if (requested >= previous) { since = double.NaN; previous = requested; }
        else if (double.IsNaN(since)) since = now;
        else if (now - since >= 14) { previous = requested; since = double.NaN; }
        return previous;
    }
    public Demand Evaluate(Reading r, double now)
    {
        string fault = null;
        if (!r.Healthy()) fault = "sensor-or-tach";
        if (r.Cpu >= 87 || r.Ccd1 >= 87 || r.Ccd2 >= 87 || r.Gpu >= 82 ||
            r.Water >= 47 || r.Disk0 >= 65 || r.Disk1 >= 65) fault = "temperature";
        if (fault != null)
        {
            rad = air = 100;
            radAt = airAt = double.NaN;
            return Demand.Full(fault);
        }
        if (r.Water >= 45) boosted = true;
        else if (r.Water <= 44.4) boosted = false;
        int top = Curve(r.Water, Rad);
        if (boosted || r.Cpu >= 85 || r.Ccd1 >= 85 || r.Ccd2 >= 85) top = Math.Max(top, 88);
        int bottom = Math.Max(Curve(r.Cpu, CpuAir), Curve(r.Gpu, GpuAir));
        return new Demand(HoldDown(top, ref rad, ref radAt, now),
            HoldDown(bottom, ref air, ref airAt, now), 35, "curves");
    }
}
