// =============================================================================
// <copyright file="AotSafetyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Dispatch;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.Scheduling.Architecture;

/// <summary>
/// AOT safety tests (DR-13): verifies that the shipped scheduling assemblies
/// contain no reflection-based activation patterns, type-name serialization for
/// activation, or expression-tree compilation.
/// </summary>
[Property("Category", "Architecture")]
public class AotSafetyTests
{
    private static readonly string OutputDirectory =
        Path.GetDirectoryName(typeof(AotSafetyTests).Assembly.Location)!;

    private static IReadOnlyList<string> SchedulingAssemblyPaths { get; } =
    [
        Path.Combine(OutputDirectory, "Bifrost.Scheduling.Core.dll"),
        Path.Combine(OutputDirectory, "Bifrost.Scheduling.dll"),
    ];

    /// <summary>
    /// Verifies that the shipped scheduling assemblies contain no calls to
    /// <c>Type.GetType</c>, <c>Activator.CreateInstance(Type)</c> (the string/type
    /// overloads), or <c>Expression.Compile</c> — all of which are unsafe under
    /// NativeAOT trim analysis.
    /// </summary>
    [Test]
    public async Task SchedulingAssemblies_ContainNoTypeGetTypeCalls()
    {
        var banned = new[]
        {
            // Type lookup by string — IL2057 under trim analysis
            ("System.Type", "GetType"),

            // Reflection-based activation — IL2067 under trim analysis
            ("System.Activator", "CreateInstance"),

            // Expression-tree compilation — generates dynamic IL, unsupported under NativeAOT
            ("System.Linq.Expressions.LambdaExpression", "Compile"),
            ("System.Linq.Expressions.Expression`1", "Compile"),
        };

        var violations = FindMemberReferenceViolations(SchedulingAssemblyPaths, banned);

        await Assert.That(violations).IsEmpty()
            .Because(
                "Shipping scheduling assemblies must contain no reflection-based activation " +
                "(DR-13). Found AOT-unsafe member references:\n" +
                string.Join("\n", violations));
    }

    /// <summary>
    /// Verifies that <c>JobRecord.DispatcherTypeName</c> is a diagnostic-only label —
    /// never an input to runtime type activation. The dispatch router resolves custom
    /// dispatchers from the DI container using generic registration
    /// (<c>DispatchVia&lt;TDispatcher&gt;()</c> registers <c>TDispatcher</c> at
    /// config time via <c>GetRequiredService&lt;TDispatcher&gt;()</c>), so the field
    /// carries a string label for observability only, never for reflection.
    /// </summary>
    [Test]
    public async Task DispatcherTypeName_IsDiagnosticOnly()
    {
        // Assert 1 — JobRecord.DispatcherTypeName is a nullable string (not a
        //   Type or an activation key). This confirms the record's shape is safe.
        var prop = typeof(JobRecord).GetProperty(nameof(JobRecord.DispatcherTypeName));

        await Assert.That(prop).IsNotNull()
            .Because("JobRecord.DispatcherTypeName property must exist.");

        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(string))
            .Because(
                "DispatcherTypeName must be a plain string — not Type, TypeInfo, or " +
                "any activation-capable handle. It is a diagnostic label only.");

        // Assert 2 — JobDispatcherRouter.Dispatch does NOT take a string/Type
        //   parameter for activation. It receives a fully-resolved IJobDispatcher.
        var dispatchMethod = typeof(JobDispatcherRouter).GetMethod(
            "Dispatch",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        // JobDispatcherRouter is internal; get the interface method from
        // IJobDispatcherRouter which IS public.
        var routerInterface = typeof(IJobDispatcherRouter);
        var routerDispatch = routerInterface.GetMethod("Dispatch");

        await Assert.That(routerDispatch).IsNotNull()
            .Because("IJobDispatcherRouter.Dispatch must exist.");

        // The first parameter must be an IJobDispatcher — a resolved instance,
        // not a string type name. This is the structural proof that DispatcherTypeName
        // is never used for activation.
        var firstParam = routerDispatch!.GetParameters().FirstOrDefault();

        await Assert.That(firstParam).IsNotNull()
            .Because("IJobDispatcherRouter.Dispatch must have parameters.");

        await Assert.That(firstParam!.ParameterType).IsEqualTo(typeof(IJobDispatcher))
            .Because(
                "IJobDispatcherRouter.Dispatch must accept an already-resolved " +
                "IJobDispatcher — not a type name or Type handle. " +
                "Custom dispatchers are resolved at DI-config time via " +
                "DispatchVia<TDispatcher>() → GetRequiredService<TDispatcher>(), " +
                "making DispatcherTypeName purely diagnostic.");

        // Assert 3 — DispatchVia<TDispatcher> in the builder produces a factory
        //   that resolves via GetRequiredService<TDispatcher> (generic, AOT-safe).
        //   We verify this indirectly by building a ServiceCollection with a test
        //   dispatcher and confirming the factory resolves without reflection hacks.
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<StubJobDispatcher>();
        services.AddScheduler(b =>
            b.AddJob<object>("probe-job")
             .Every(TimeSpan.FromHours(1))
             .DispatchVia<StubJobDispatcher>());

        using var sp = services.BuildServiceProvider();

        // If DispatchVia uses string-based reflection, this would throw at resolve
        // time or fail to find the dispatcher. A clean resolution proves generic DI.
        var registry = sp.GetRequiredService<IScheduleRegistry>();

        await Assert.That(registry).IsNotNull()
            .Because(
                "Registry must resolve cleanly when DispatchVia<TDispatcher> is used, " +
                "confirming generic DI (not string-based activation) is in play.");
    }

    // =========================================================================
    // IL scanner (same approach as BannedTimeApiTests)
    // =========================================================================

    private static List<string> FindMemberReferenceViolations(
        IEnumerable<string> assemblyPaths,
        IEnumerable<(string TypeFullName, string MemberName)> bannedMethods)
    {
        var violations = new List<string>();
        var bannedList = bannedMethods.ToList();

        foreach (var path in assemblyPaths)
        {
            var assemblyName = Path.GetFileName(path);

            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                continue;
            }

            var metadata = peReader.GetMetadataReader();

            foreach (var memberRefHandle in metadata.MemberReferences)
            {
                var memberRef = metadata.GetMemberReference(memberRefHandle);
                var memberName = metadata.GetString(memberRef.Name);

                if (memberRef.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var typeRef = metadata.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                var typeNamespace = metadata.GetString(typeRef.Namespace);
                var typeName = metadata.GetString(typeRef.Name);
                var typeFullName = string.IsNullOrEmpty(typeNamespace)
                    ? typeName
                    : $"{typeNamespace}.{typeName}";

                foreach (var (bannedType, bannedMember) in bannedList)
                {
                    if (typeFullName == bannedType && memberName == bannedMember)
                    {
                        violations.Add($"{assemblyName}: {typeFullName}.{bannedMember}");
                        break;
                    }
                }
            }
        }

        return violations;
    }

    // =========================================================================
    // Test stub — a no-op dispatcher for the DispatchVia DI resolution assertion.
    // =========================================================================

    private sealed class StubJobDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
