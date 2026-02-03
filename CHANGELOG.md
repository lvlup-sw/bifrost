# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/lvlup-sw/bifrost/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/lvlup-sw/bifrost/releases/tag/v0.1.0
