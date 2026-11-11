using Bifrost.Scheduling.Core;
using Bifrost;
using Bifrost.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;

static class Lifecycle
{
    static WorkOrchestrator<int> Create(int capacity) => new(new Handler(), Options.Create(new WorkOrchestratorOptions { Capacity=capacity, WorkerCount=0, FullMode=QueueFullMode.Reject }), NullLogger<WorkOrchestrator<int>>.Instance);
    public static void Explore(bool negative)
    {
        using var engine=TestingEngine.Create(Configuration.Create().WithTestingIterations(100).WithRandomGeneratorSeed(17), async () => {
            if(negative)
            {
                int value=0;
                var writer=Task.Run(()=>value=1);
                Microsoft.Coyote.Specifications.Specification.Assert(value==0,"negative schedule control");
                await writer;
                return;
            }
            await using var q=Create(2);
            var producer=Task.Run(()=>q.Enqueue(1));
            var closer=Task.Run(()=>q.DrainAsync());
            await Task.WhenAll(new Task[] { producer,closer });
            Microsoft.Coyote.Specifications.Specification.Assert(!q.Enqueue(2).IsAccepted,"admission after close");
        });
        engine.Run();
        Console.WriteLine(engine.GetReport());
        foreach(var invocation in engine.TestReport.UncontrolledInvocations) Console.WriteLine($"UNCONTROLLED: {invocation}");
        Console.WriteLine(engine.ReadableTrace);
    }
    public static async Task Trace()
    {
        foreach(var heldout in new[]{false,true})
        {
            using var output=new StreamWriter($"tools/invariant-pilots/results/{(heldout?"heldout":"training")}.dtrace");
            output.WriteLine("decl-version 2.0\nvar-comparability none\n");
            foreach(var outcome in new[]{"Accepted","Rejected","Canceled","Exception"})
            {
                output.WriteLine($"ppt Bifrost.Admission.{outcome}:::POINT\nppt-type point");
                foreach(var variable in new[]{"capacity","before","after","delta"}) output.WriteLine($"variable {variable}\n  var-kind variable\n  dec-type int\n  rep-type int\n  comparability -1");
                output.WriteLine();
            }
            foreach(int capacity in heldout?new[]{3,7,16}:new[]{1,2})
            for(int repetition=0;repetition<12;repetition++)
            {
                await using var q=Create(capacity);
                for(int i=0;i<capacity+1;i++)
                {
                    int before=q.PendingCount;
                    var result=q.Enqueue(i);
                    Write(result.IsAccepted?"Accepted":"Rejected",before,q.PendingCount);
                }
                int full=q.PendingCount;
                using var cts=new CancellationTokenSource(); cts.Cancel();
                try { await q.EnqueueAsync(99,ct:cts.Token); }
                catch(OperationCanceledException) { Write("Canceled",full,q.PendingCount); }
                try { _=q.Enqueue(99,new WorkEnqueueOptions((WorkClass)999)); }
                catch(ArgumentOutOfRangeException) { Write("Exception",full,q.PendingCount); }
                void Write(string outcome,int before,int after)
                {
                    output.WriteLine($"Bifrost.Admission.{outcome}:::POINT");
                    foreach(var pair in new[]{("capacity",capacity),("before",before),("after",after),("delta",after-before)}) output.WriteLine($"{pair.Item1}\n{pair.Item2}\n1");
                    output.WriteLine();
                }
            }
        }
    }
    sealed class Handler:IWorkHandler<int> { public ValueTask HandleAsync(int work,CancellationToken ct)=>ValueTask.CompletedTask; }
}
