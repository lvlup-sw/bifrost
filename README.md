# Bifrost

[![Build](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml/badge.svg)](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

A high-performance, production-ready Channel-based work orchestration library for .NET 10. Provides bounded work queues, worker pool management, autoscaling, resilience, and comprehensive observability.

## Features

- **Zero-allocation hot paths** - `ValueTask`-based enqueue with no per-item allocations
- **Autoscaling** - Dynamic worker scaling based on queue utilization with configurable watermarks
- **Resilience** - Polly integration for retry, timeout, and circuit breaker patterns
- **Health Checks** - ASP.NET Core health check integration
- **OpenTelemetry** - Metrics and tracing support
- **Event Streaming** - Pub/sub events for work lifecycle tracking

## Packages

| Package | Description | NuGet |
|---------|-------------|-------|
| `Bifrost.Core` | Core abstractions with zero dependencies | - |
| `Bifrost` | Main implementation with Channel-based orchestration | - |
| `Bifrost.HealthChecks` | ASP.NET Core health check integration | - |
| `Bifrost.OpenTelemetry` | OpenTelemetry metrics support | - |
| `Bifrost.Resilience` | Polly resilience integration | - |

## Quick Start

### Installation

```bash
dotnet add package Bifrost
dotnet add package Bifrost.HealthChecks  # optional
dotnet add package Bifrost.OpenTelemetry  # optional
dotnet add package Bifrost.Resilience  # optional
```

### Basic Usage

```csharp
// 1. Define your work type
public record EmailJob(string To, string Subject, string Body);

// 2. Implement a handler
public class EmailHandler : IWorkHandler<EmailJob>
{
    private readonly IEmailService _emailService;

    public EmailHandler(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public async ValueTask HandleAsync(EmailJob job, CancellationToken ct)
    {
        await _emailService.SendAsync(job.To, job.Subject, job.Body, ct);
    }
}

// 3. Register services
services.AddWorkOrchestrator<EmailJob>(options =>
{
    options.Capacity = 128;
    options.WorkerCount = 2;
})
.WithHandler<EmailHandler>();

// 4. Enqueue work
public class MyService
{
    private readonly IWorkOrchestrator<EmailJob> _orchestrator;

    public MyService(IWorkOrchestrator<EmailJob> orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task SendWelcomeEmailAsync(string email)
    {
        await _orchestrator.EnqueueAsync(
            new EmailJob(email, "Welcome!", "Thanks for signing up!"));
    }
}
```

### With Autoscaling

```csharp
services.AddWorkOrchestrator<EmailJob>(options =>
{
    options.Capacity = 128;
    options.WorkerCount = 2;
})
.WithHandler<EmailHandler>()
.WithAutoscaling(scaling =>
{
    scaling.MinWorkers = 1;
    scaling.MaxWorkers = 16;
    scaling.HighWatermark = 0.8;
    scaling.LowWatermark = 0.3;
    scaling.CooldownPeriod = TimeSpan.FromSeconds(30);
});
```

### With Health Checks

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithHealthChecks();

// In your health check endpoint configuration
app.MapHealthChecks("/health");
```

### With OpenTelemetry

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithOpenTelemetry();
```

### With Resilience (Polly)

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithResilience(resilience =>
    {
        resilience.RetryCount = 3;
        resilience.Timeout = TimeSpan.FromSeconds(30);
        resilience.UseExponentialBackoff = true;
    });
```

## API Overview

### IWorkOrchestrator<TWork>

The main interface for enqueuing work:

```csharp
public interface IWorkOrchestrator<TWork> : IAsyncDisposable
{
    // Core operations - zero-allocation hot path
    ValueTask EnqueueAsync(TWork work, CancellationToken ct = default);
    bool TryEnqueue(TWork work);

    // Observability (non-allocating property access)
    int PendingCount { get; }
    int ActiveWorkers { get; }
    int Capacity { get; }

    // Lifecycle
    Task StopAsync(CancellationToken ct = default);

    // Escape hatch for advanced scenarios
    ChannelWriter<TWork> Writer { get; }
}
```

### IWorkHandler<TWork>

Implement this interface to handle work items:

```csharp
public interface IWorkHandler<TWork>
{
    ValueTask HandleAsync(TWork work, CancellationToken ct);
}
```

### Event Streaming

Subscribe to orchestrator events:

```csharp
// Enable event streaming
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithEventStream();

// Subscribe to events
var eventOrchestrator = serviceProvider
    .GetRequiredService<IEventStreamOrchestrator<EmailJob>>();

await foreach (var evt in eventOrchestrator.GetEventStreamAsync<WorkCompletedEvent<EmailJob>>(ct))
{
    Console.WriteLine($"Work completed in {evt.Duration}");
}
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/lvlup-sw/bifrost.git
cd bifrost

# Build
dotnet build

# Run tests
dotnet test
```

## Design

See the [design document](docs/design/channel-orchestrator-library.md) for architectural details and implementation notes.

## License

This project is licensed under the Apache License 2.0 - see the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
