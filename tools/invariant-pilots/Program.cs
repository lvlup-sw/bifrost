using FsCheck;
using FsCheck.Fluent;
using Bifrost.Scheduling.Core;

if(args.FirstOrDefault()=="trace") { await Lifecycle.Trace(); return; }
if(args.FirstOrDefault()=="coyote") { Lifecycle.Explore(args.Contains("negative")); return; }
var gen = from interval in Gen.Choose(1,1000000)
          from last in Gen.Choose(0,1000000)
          from steps in Gen.Choose(0,10000)
          select new Sample(interval,last,steps);
IEnumerable<Sample> Shrink(Sample x)
{
    if(x.Interval > 1) yield return x with { Interval = 1 };
    if(x.Last > 0) yield return x with { Last = 0 };
    for(int n=0;n<x.Steps;n++) yield return x with { Steps=n };
}
var arb = Arb.From(gen, Shrink);
var count=0;
Prop.ForAll(arb, x => {
    count++;
    long now=x.Last+(long)x.Interval*x.Steps;
    var actual=new IntervalCadence(TimeSpan.FromTicks(x.Interval)).ComputeNextFire(new(x.Last,TimeSpan.Zero),new(now,TimeSpan.Zero));
    return actual!.Value.Ticks==now+x.Interval;
}).Check(Config.QuickThrowOnFailure.WithMaxTest(1000));
Console.WriteLine($"FsCheck actual API: {count} valid checks");
Prop.ForAll(arb, x => {
    long now=x.Last+(long)x.Interval*x.Steps;
    var actual=new IntervalCadence(TimeSpan.FromTicks(x.Interval)).ComputeNextFire(new(x.Last,TimeSpan.Zero),new(now,TimeSpan.Zero));
    long cappedOracle=x.Last+(long)x.Interval*(Math.Min(x.Steps,100)+1);
    return actual!.Value.Ticks==cappedOracle;
}).Check(Config.Quick.WithMaxTest(1000));
record Sample(int Interval,int Last,int Steps);
