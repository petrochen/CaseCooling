using System;
using System.Collections.Generic;
using System.IO;

internal static class Ring
{
    public const int Seconds=86400, Header=16, Size=16+4*(int)K.Count;
    public const long Magic=0x31474E4952434343L; // CaseCooling ring v1
    public static readonly DateTime Epoch=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
    public static long Stamp(DateTime utc){return (utc.Ticks-Epoch.Ticks)/TimeSpan.TicksPerSecond;}
    public static long Position(long stamp){return Header+(stamp%Seconds)*Size;}
    public static string Installed {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"CaseCooling","history-1s.bin");}}
    public static void Verify(FileStream f)
    {
        f.Position=0;
        using(var reader=new BinaryReader(f,System.Text.Encoding.UTF8,true))
            if(f.Length!=Header+Seconds*Size || reader.ReadInt64()!=Magic || reader.ReadInt32()!=Size || reader.ReadInt32()!=Seconds)
                throw new InvalidDataException("Unexpected CaseCooling history format.");
    }
}

internal sealed class RingWriter : IDisposable
{
    readonly FileStream f;
    readonly byte[] bytes=new byte[Ring.Size];
    readonly float[] values=new float[(int)K.Count];
    long previous;
    internal RingWriter(string path,long now)
    {
        f=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
        try {
            if(f.Length==0)
            {
                using(var writer=new BinaryWriter(f,System.Text.Encoding.UTF8,true))
                {writer.Write(Ring.Magic);writer.Write(Ring.Size);writer.Write(Ring.Seconds);}
                f.SetLength(Ring.Header+Ring.Seconds*Ring.Size);
            }
            Ring.Verify(f); Expire(now);
        } catch {f.Dispose();throw;}
    }
    // Also erase expired slots after sleep, downtime or a wall-clock adjustment.
    void Expire(long now)
    {
        f.Position=Ring.Header;
        byte[] block=new byte[Ring.Size*480];
        for(int i=0;i<Ring.Seconds;i+=480)
        {
            long pos=f.Position; int n=0;
            while(n<block.Length){int got=f.Read(block,n,block.Length-n);if(got==0)throw new EndOfStreamException();n+=got;}
            bool changed=false;
            for(int off=0;off<block.Length;off+=Ring.Size)
            {
                long stamp=BitConverter.ToInt64(block,off);
                if(stamp==0 && BitConverter.ToInt64(block,off+Ring.Size-8)==0)continue;
                if(stamp<=now-Ring.Seconds || stamp>now || Ring.Position(stamp)!=pos+off || BitConverter.ToInt64(block,off+Ring.Size-8)!=stamp)
                {Array.Clear(block,off,Ring.Size);changed=true;}
            }
            if(changed){f.Position=pos;f.Write(block,0,block.Length);}
        }
        f.Flush();
    }
    internal void Put(long stamp,Sample row,bool historical=false)
    {
        if(!historical && previous!=0 && stamp!=previous+1 && stamp!=previous) Expire(stamp);
        previous=stamp;
        for(int k=0;k<values.Length;k++)values[k]=(float)row.Values[k];
        Buffer.BlockCopy(BitConverter.GetBytes(stamp),0,bytes,0,8);
        Buffer.BlockCopy(values,0,bytes,8,values.Length*4);
        Buffer.BlockCopy(bytes,0,bytes,bytes.Length-8,8);
        f.Position=Ring.Position(stamp);f.Write(bytes,0,bytes.Length);
        if(!historical)f.Flush();
    }
    public void Dispose(){f.Dispose();}
}

internal sealed class RingReader
{
    readonly List<Sample> rows=new List<Sample>();
    long lastPoll;
    public List<Sample> Read(string path,long now)
    {
        using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            Ring.Verify(f);
            long low=now-Ring.Seconds+1;
            if(now<lastPoll || lastPoll<low)rows.Clear();
            int old=0;
            while(old<rows.Count && Ring.Stamp(rows[old].Time)<low)old++;
            if(old>0)rows.RemoveRange(0,old);
            // Retry the last second if the viewer polled before the writer committed.
            long first=Math.Max(low, rows.Count==0?low:Ring.Stamp(rows[rows.Count-1].Time)+1);
            if(rows.Count==0 && lastPoll>=low && now>=lastPoll)first=Math.Max(low,lastPoll-1);
            using(var reader=new BinaryReader(f))
            {
                while(first<=now)
                {
                    int count=(int)Math.Min(now-first+1,Ring.Seconds-first%Ring.Seconds);
                    f.Position=Ring.Position(first);
                    byte[] bytes=reader.ReadBytes(count*Ring.Size);
                    for(int i=0;i<count;i++){
                        long stamp=first+i;int off=i*Ring.Size;
                        if(BitConverter.ToInt64(bytes,off)!=stamp || BitConverter.ToInt64(bytes,off+Ring.Size-8)!=stamp)continue;
                        var sample=new Sample{Time=Ring.Epoch.AddSeconds(stamp)};
                        for(int k=0;k<(int)K.Count;k++)sample.Values[k]=BitConverter.ToSingle(bytes,off+8+k*4);
                        rows.Add(sample);
                    }
                    first+=count;
                }
            }
            lastPoll=now;
        }
        return new List<Sample>(rows);
    }
}
