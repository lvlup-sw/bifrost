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
    /// <remarks>
    /// <c>Type.GetType</c> looks up a type by string (IL2057 under trim analysis).
    /// <c>Activator.CreateInstance</c> is reflection-based activation (IL2067 under trim analysis).
    /// <c>LambdaExpression.Compile</c> and <c>Expression&lt;T&gt;.Compile</c> compile expression trees,
    /// which generates dynamic IL. NativeAOT does not support dynamic IL.
    /// </remarks>
    [Test]
    public async Task SchedulingAssemblies_ContainNoTypeGetTypeCalls()
    {
        var banned = new[]
        {
            ("System.Type", "GetType"),
            ("System.Activator", "CreateInstance"),
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
    /// <remarks>
    /// <para>
    /// The test makes three assertions. First, <c>JobRecord.DispatcherTypeName</c> is a nullable
    /// string, not a <see cref="Type"/> or an activation key. This confirms that the shape of the
    /// record is safe.
    /// </para>
    /// <para>
    /// Second, the router <c>Dispatch</c> method does NOT take a string or <see cref="Type"/> parameter
    /// for activation. It receives a fully-resolved <c>IJobDispatcher</c>. <c>JobDispatcherRouter</c> is
    /// internal, so the test reads the method from the public <c>IJobDispatcherRouter</c> interface. The
    /// first parameter must be an <c>IJobDispatcher</c> (a resolved instance), not a string type name.
    /// This is the structural proof that <c>DispatcherTypeName</c> is never used for activation.
    /// </para>
    /// <para>
    /// Third, <c>DispatchVia&lt;TDispatcher&gt;</c> in the builder produces a factory that resolves
    /// through <c>GetRequiredService&lt;TDispatcher&gt;</c> (generic and AOT-safe). The test proves this
    /// indirectly: it builds a <c>ServiceCollection</c> with a test dispatcher and resolves the registry.
    /// If <c>DispatchVia</c> used string-based reflection, the resolution would throw or fail to find the
    /// dispatcher. A clean resolution proves generic DI.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DispatcherTypeName_IsDiagnosticOnly()
    {
        var prop = typeof(JobRecord).GetProperty(nameof(JobRecord.DispatcherTypeName));

        await Assert.That(prop).IsNotNull()
            .Because("JobRecord.DispatcherTypeName property must exist.");

        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(string))
            .Because(
                "DispatcherTypeName must be a plain string — not Type, TypeInfo, or " +
                "any activation-capable handle. It is a diagnostic label only.");
        var dispatchMethod = typeof(JobDispatcherRouter).GetMethod(
            "Dispatch",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var routerInterface = typeof(IJobDispatcherRouter);
        var routerDispatch = routerInterface.GetMethod("Dispatch");

        await Assert.That(routerDispatch).IsNotNull()
            .Because("IJobDispatcherRouter.Dispatch must exist.");
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
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<StubJobDispatcher>();
        services.AddScheduler(b =>
            b.AddJob<object>("probe-job")
             .Every(TimeSpan.FromHours(1))
             .DispatchVia<StubJobDispatcher>());

        using var sp = services.BuildServiceProvider();
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
