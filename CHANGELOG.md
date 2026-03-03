# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] - 2026-03-02

### Added

- **Dead Letter Queue** - Failed work items are routed to a dead letter queue instead of being silently dropped
  - `IDeadLetterQueue<TWork>` abstraction in Bifrost.Core with `EnqueueAsync`, `ReadAllAsync`, and `Count`
  - `DeadLetteredWork<TWork>` record capturing exception, attempt count, failure time, and correlation ID
  - `DeadLetterQueue<TWork>` channel-backed implementation with configurable capacity
  - `DeadLetterHandler<TWork>` handler decorator with retry-then-dead-letter routing
  - `.WithDeadLetterQueue()` builder extension for fluent configuration
  - `WorkDeadLetteredEvent<TWork>` event type for event stream integration
  - `DeadLetterQueueHealthCheck` with three-tier status (Healthy / Degraded / Unhealthy)
  - `orchestrator.items.deadlettered` counter and `orchestrator.dlq.depth` gauge in OpenTelemetry metrics
  - DLQ benchmarks (`DeadLetterQueueBenchmarks`) covering enqueue, drain, happy path, and failure path

## [0.2.0] - 2026-02-07

### Added

- **Benchmark Suite** (`Bifrost.Benchmarks`) - BenchmarkDotNet performance validation infrastructure
  - Core enqueue and throughput benchmarks
  - Allocation validation benchmarks (zero-alloc hot path verification)
  - Decorator overhead benchmarks
  - Autoscaling benchmarks
  - Benchmark documentation with run instructions and baseline results
  - CI smoke test (`--job Dry`) for benchmark validation

### Changed

- Eliminated event stream struct boxing for zero-allocation event dispatch
- Fixed `Run_Sync` benchmark and documented `EnqueueAsync` capacity behavior

### Fixed

- CI formatting check and format enforcement
- Test timing reliability improvements

## [0.1.0] - 2026-02-02

### Added

- **LevelUp.Bifrost.Core** - Core primitives and contracts for channel-based work orchestration
  - `Result<T>` and `Error` types for functional error handling
  - `IProcessExecutor<T>` contract for process execution
  - `ProcessConfiguration` and `AutoscalingOptions` for configuration
  - Event types for streaming (`EventMessage`, `EventData`, `ProgressEventData`)

- **LevelUp.Bifrost** - Main library with work orchestration infrastructure
  - `TaskOrchestrator` - Background task scheduling with resilience
  - `AutoscalingEngine` - Dynamic worker scaling based on queue utilization
  - `ResiliencyPolicyGenerator` - Polly policy factory for retry/timeout/circuit breaker
  - `WorkerRegistry` - Tracks active workers for autoscaling

- **LevelUp.Bifrost.Resilience** - Polly integration for resilient operations
  - Pre-configured resilience policies
  - Circuit breaker patterns
  - Retry with exponential backoff

- **LevelUp.Bifrost.HealthChecks** - Health check implementations
  - Autoscaling health checks
  - Event streaming health checks
  - Worker registry health checks

- **LevelUp.Bifrost.OpenTelemetry** - Observability integration
  - Metrics for work orchestration
  - Tracing support
  - Diagnostic listeners

### Changed

- Renamed from `Levelup.Channels` to `Bifrost` per naming convention decision
- Restructured repository to follow lvlup-sw conventions (src/ layout)

[Unreleased]: https://github.com/lvlup-sw/bifrost/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/lvlup-sw/bifrost/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/lvlup-sw/bifrost/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lvlup-sw/bifrost/releases/tag/v0.1.0
