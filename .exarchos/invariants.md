---
# Invariant catalog (user tier) — authored via `exarchos invariants scaffold`.
#
# Each entry expresses one architectural rule. Author new entries with
# `exarchos invariants add` (validates the entry shape before writing), or
# un-comment + edit the worked example below. Validate the resolved catalog
# with `exarchos doctor` (invariants-catalog check) and inspect it with the
# `invariants_effective` view. Authoring guide:
# docs/guides/authoring-invariants.md.
#
# Consumers always use the `user` tier (`U-N` ids) — this catalog is your
# project's own. The `dev` tier (`INV-N`) is exarchos-internal: it is
# exarchos's own reserved substrate namespace and collides with its built-in
# `INV-*` if reused from a consumer repo.
#
# Worked example (un-comment to start). `mode: audit` is pure judgment — an
# LLM evaluates the prompt against the diff; always portable (INV-6). For a
# declarative grep/structural check, use `mode: check` with a combinator tree
# (see the authoring guide).
schema-version: 3
invariants:
  [
    {
        id: U-1,
        dimension: admission-contract,
        axis: substrate,
        cost-of-load: always-load,
        applies-to: [ src/Bifrost/**/*.cs, src/Bifrost.Core/**/*.cs ],
        summary: "Admission paths surface capacity, watermark, and shutdown as a
          rejected EnqueueResult; the only exception they may throw is
          OperationCanceledException from a canceled caller token.",
        references: [ docs/designs/2026-02-02-roadmap-to-0.5.0.md, CHANGELOG.md ],
        phase-affinity: [ review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff let an admission path
              (EnqueueAsync/TryEnqueue/Run/TryRun, or any orchestrator decorator
              forwarding them) signal capacity, watermark, or shutdown by
              THROWING instead of returning a rejected EnqueueResult? The only
              exception an enqueue may throw is OperationCanceledException from
              a canceled caller token. Flag any new throw for
              back-pressure/shutdown, any path that both returns Rejected and
              throws, or caller cancellation mapped to Rejected(Shutdown). Cite
              file + line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-2,
        dimension: admission-contract,
        axis: substrate,
        cost-of-load: always-load,
        applies-to: [ src/Bifrost/**/*.cs, src/Bifrost.Core/**/*.cs ],
        summary: "Work enters only through the orchestrator's admission methods; no API
          exposes the raw queue, a ChannelWriter, or an IWorkQueue binding to
          bypass admission policy.",
        references: [ docs/designs/2026-02-02-roadmap-to-0.5.0.md, CHANGELOG.md ],
        phase-affinity: [ plan, review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff expose a way to bypass admission policy? Flag any
              new member on IWorkOrchestrator<TWork> or its implementations that
              returns the raw queue, a ChannelWriter<T>, or an IWorkQueue<T>
              binding; any IWorkQueue<T> binding made public rather than
              internal sealed; or any producer entry point that writes work
              without going through the admission methods
              (capacity/watermark/classification/DLQ routing). Cite file +
              line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-3,
        dimension: package-layering,
        axis: substrate,
        cost-of-load: reference-only,
        applies-to: [ src/**/*.csproj ],
        summary: "Core and Concurrency are dependency-free leaves, the main package
          references only those two, and integration packages never reference
          each other.",
        references: [ CLAUDE.md, README.md ],
        phase-affinity: [ plan, review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff add a <ProjectReference> that violates Bifrost's
              package layering? Rules: Bifrost.Core, Bifrost.Concurrency, and
              Bifrost.Scheduling.Core reference NO other Bifrost package; the
              main Bifrost package references only Bifrost.Core and
              Bifrost.Concurrency; integration packages (HealthChecks,
              OpenTelemetry, Resilience) reference only Bifrost/Bifrost.Core and
              never each other; Bifrost.Scheduling references only
              Scheduling.Core, Bifrost.Core, Bifrost. Flag any new
              ProjectReference that crosses these boundaries. Cite the .csproj +
              line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-4,
        dimension: dispatch-strategy,
        axis: substrate,
        cost-of-load: reference-only,
        applies-to:
          [
            src/Bifrost/**/*.cs,
            src/Bifrost.Concurrency/**/*.cs,
            src/Bifrost.Core/**/*.cs
          ],
        summary: "Dispatch strategies and priority bindings are selected through closed
          enums and a construction-time factory switch, never reflection or
          codegen, and bindings stay internal sealed.",
        references:
          [
            docs/designs/2026-06-12-cpq-port-priority-dispatch.md,
            docs/designs/2026-06-15-cpq-binding-auto-select.md
          ],
        phase-affinity: [ review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff select or construct a dispatch strategy / priority
              binding using reflection, Activator.CreateInstance, Type.GetType,
              or runtime codegen instead of the DispatchStrategy/PriorityBinding
              enums plus the construction-time factory switch? Flag also: a new
              binding that is not internal sealed, binding resolution moved out
              of the constructor to enqueue-time, or a new strategy added
              without a corresponding enum value. Cite file + line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-5,
        dimension: failure-observability,
        axis: substrate,
        cost-of-load: always-load,
        applies-to:
          [
            src/Bifrost/**/*.cs,
            src/Bifrost.Resilience/**/*.cs,
            src/Bifrost.Scheduling/**/*.cs
          ],
        summary: "Every failed, rejected, or shed item is observable - routed, returned
          as a result, or counted and logged - never silently dropped or
          swallowed.",
        references: [ docs/designs/2026-02-20-dead-letter-queues.md, CHANGELOG.md ],
        phase-affinity: [ review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff introduce a path where failed, rejected, or shed
              work becomes UNOBSERVABLE? Flag any empty/swallowing catch that
              discards an exception without logging or rethrowing; any drop of a
              work item, event, or dead-letter entry that is not either routed
              onward or counted+logged (e.g. DropOldest without incrementing a
              dropped-count and logging); or a subscriber/handler error silently
              ignored. Every shed item must be routed, returned as a result, or
              counted+logged. Cite file + line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-6,
        dimension: resilience,
        axis: substrate,
        cost-of-load: reference-only,
        applies-to: [ src/Bifrost/**/*.cs, src/Bifrost.Resilience/**/*.cs ],
        summary: "Resilience policies retry only genuinely transient faults; admission
          and business-logic failures (rejections, WorkRejectedException, caller
          cancellation) are never retried.",
        references: [ docs/designs/2026-02-20-dead-letter-queues.md, CHANGELOG.md ],
        phase-affinity: [ review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff let the resilience layer retry a non-transient
              fault? Polly retry/circuit-breaker policies must retry only
              genuinely transient exceptions; admission/business-logic failures
              - WorkRejectedException, capacity/watermark rejections, and
              OperationCanceledException from caller cancellation - must never
              be added to the retried/transient set. Flag any change to the
              transient-exception predicate that admits these. Cite file +
              line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      },
    {
        id: U-7,
        dimension: extensibility,
        axis: substrate,
        cost-of-load: always-load,
        applies-to: [ src/Bifrost/**/*.cs ],
        summary: "Cross-cutting concerns are handler/orchestrator decorators registered
          through WorkOrchestratorBuilder with a unique Order, wrapping the core
          - never baked into the core execution path.",
        references: [ docs/designs/2026-03-15-api-surface-improvements.md, CHANGELOG.md ],
        phase-affinity: [ plan, review ],
        enforcement:
          {
            mode: audit,
            audit-prompt: "Does this diff add a cross-cutting concern (logging, metrics,
              tracing, retry, throttling, event publication, dead-lettering,
              autoscaling, etc.) by modifying the core WorkOrchestrator / core
              handler-invocation path instead of implementing it as a handler or
              orchestrator decorator registered through WorkOrchestratorBuilder
              with a unique Order? New behavior that wraps work execution must
              compose as an ordered decorator around the core kernel, not be
              baked into it. Cite file + line."
          },
        severity: { default: blocking, by-workflow: { oneshot: advisory } },
        integrity-class: user
      }
  ]

#   - id: U-1
#     dimension: example-dimension
#     axis: authoring
#     cost-of-load: reference-only
#     applies-to:
#       - "src/**/*.ts"
#     summary: One-sentence statement of the rule this invariant enforces.
#     references:
#       - docs/architecture/some-design.md
#     severity:
#       default: advisory
#     integrity-class: user
#     enforcement:
#       mode: audit
#       audit-prompt: >-
#         Does the diff violate <the rule>? Cite the offending file + line.
---
