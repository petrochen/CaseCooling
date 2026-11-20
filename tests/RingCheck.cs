using System;
using System.IO;
using System.Linq;
class RingCheck
{
    static void Test(bool pass,string what){if(!pass)throw new Exception(what);}
    static void Main(string[] args){
        string p=Path.Combine(Path.GetTempPath(),"CaseCooling-"+Guid.NewGuid().ToString("N")+".ring");
        long now=1780000000;var s=new Sample();s.Values[(int)K.Cpu]=59.5;
        using(var w=new RingWriter(p,now)){
            w.Put(now-Ring.Seconds,s,true);w.Put(now-Ring.Seconds+1,s,true);
            w.Put(now-4,s,true);w.Put(now-1,s,true);w.Put(now,s);
            var reader=new RingReader();
            var rows=reader.Read(p,now);
            Test(rows.Count==4 && rows[0].Time==Ring.Epoch.AddSeconds(now-Ring.Seconds+1),"Boundary/overwrite");
            w.Put(now+1,s);rows=reader.Read(p,now+1);Test(rows.Count==4 && rows.Last()[K.Cpu]==59.5,"Incremental");
            w.Put(now+Ring.Seconds+5,s);
        }
        using(var stream=File.OpenRead(p)){
            Test(stream.Length==Ring.Header+Ring.Seconds*Ring.Size,"Fixed storage");
            stream.Position=Ring.Position(now);byte[] old=new byte[Ring.Size];stream.Read(old,0,old.Length);Test(old.All(x=>x==0),"Expired bytes after gap");
        }
        using(var w=new RingWriter(p,now)){w.Put(now,s);}
        var current=new RingReader().Read(p,now);Test(current.Count==1,"Backward clock");
        File.Delete(p);Console.WriteLine("PASS: 86400-second retention, wrap, gap erasure, clock change, concurrent reads.");
    }
}
