// =============================================================================
// <copyright file="ResiliencyPolicyGeneratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Data.Common;
using System.Net.Sockets;

using Bifrost.Resilience;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Polly.Timeout;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResiliencyPolicyGenerator"/> utility.
/// </summary>
[Property("Category", "Unit")]
public class ResiliencyPolicyGeneratorTests
{
    private ILogger _logger = null!;
    private ResiliencySettings _settings = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _logger = NullLogger.Instance;
        _settings = new ResiliencySettings();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies GetTransientExceptionTypes returns expected exception types.
    /// </summary>
    [Test]
    public async Task GetTransientExceptionTypes_ReturnsExpectedTypes()
    {
        // Act
        var types = ResiliencyPolicyGenerator.GetTransientExceptionTypes();

        // Assert
        await Assert.That(types).Contains(typeof(HttpRequestException));
        await Assert.That(types).Contains(typeof(TimeoutException));
        await Assert.That(types).Contains(typeof(SocketException));
        await Assert.That(types).Contains(typeof(IOException));
        await Assert.That(types).Contains(typeof(DbException));
        await Assert.That(types).Contains(typeof(InvalidOperationException));
        await Assert.That(types).Contains(typeof(TimeoutRejectedException));
    }

    /// <summary>
    /// Verifies GeneratePolicy throws on null logger.
    /// </summary>
    [Test]
    public async Task GeneratePolicy_NullLogger_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResiliencyPolicyGenerator.GeneratePolicy<string>(null!, _settings, "fallback"))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies GeneratePolicy throws on null settings.
    /// </summary>
    [Test]
    public async Task GeneratePolicy_NullSettings_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResiliencyPolicyGenerator.GeneratePolicy<string>(_logger, null!, "fallback"))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies GeneratePolicy returns valid policy.
    /// </summary>
    [Test]
    public async Task GeneratePolicy_ValidInputs_ReturnsPolicy()
    {
        // Act
        var policy = ResiliencyPolicyGenerator.GeneratePolicy<string>(_logger, _settings, "fallback");

        // Assert
        await Assert.That(policy).IsNotNull();
    }

    /// <summary>
    /// Verifies GeneratePolicy returns fallback value on handled exception.
    /// </summary>
    [Test]
    public async Task GeneratePolicy_HandledException_ReturnsFallbackValue()
    {
        // Arrange
        var settings = new ResiliencySettings
        {
            RetryCount = 0,
            TimeoutIntervalSeconds = 1
        };
        var policy = ResiliencyPolicyGenerator.GeneratePolicy<string>(_logger, settings, "fallback");

        // Act
        var result = await policy.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            throw new HttpRequestException("Test exception");
        }, CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsEqualTo("fallback");
    }

    /// <summary>
    /// Verifies GeneratePolicy executes action successfully.
    /// </summary>
    [Test]
    public async Task GeneratePolicy_SuccessfulAction_ReturnsResult()
    {
        // Arrange
        var policy = ResiliencyPolicyGenerator.GeneratePolicy<string>(_logger, _settings, "fallback");

        // Act
        var result = await policy.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return "success";
        }, CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsEqualTo("success");
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern throws on null logger.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_NullLogger_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(null!, _settings, _ => true))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern throws on null settings.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_NullSettings_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(_logger, null!, _ => true))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern throws on null predicate.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_NullPredicate_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(_logger, _settings, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern returns valid policy.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_ValidInputs_ReturnsPolicy()
    {
        // Act
        var policy = ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(_logger, _settings, _ => true);

        // Assert
        await Assert.That(policy).IsNotNull();
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern executes action successfully.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_SuccessfulAction_Completes()
    {
        // Arrange
        var policy = ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(_logger, _settings, _ => true);
        var executed = false;

        // Act
        await policy.ExecuteAsync(async (_, ct) =>
        {
            await Task.Yield();
            executed = true;
        }, new Polly.Context(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(executed).IsTrue();
    }

    /// <summary>
    /// Verifies GetAsyncBackgroundTaskPattern propagates exceptions after retries.
    /// </summary>
    [Test]
    public async Task GetAsyncBackgroundTaskPattern_FailedAction_PropagatesException()
    {
        // Arrange
        var settings = new ResiliencySettings
        {
            RetryCount = 0,
            TimeoutIntervalSeconds = 1
        };
        var policy = ResiliencyPolicyGenerator.GetAsyncBackgroundTaskPattern(
            _logger,
            settings,
            ex => ex is HttpRequestException);

        // Act & Assert
        await Assert.That(async () =>
        {
            await policy.ExecuteAsync(async (_, ct) =>
            {
                await Task.Yield();
                throw new HttpRequestException("Test exception");
            }, new Polly.Context(), CancellationToken.None).ConfigureAwait(false);
        }).Throws<HttpRequestException>();
    }

    /// <summary>
    /// Verifies CreateExceptionPredicate handles null input.
    /// </summary>
    [Test]
    public async Task CreateExceptionPredicate_NullTypes_UsesTransientTypes()
    {
        // Act
        var predicate = ResiliencyPolicyGenerator.CreateExceptionPredicate(null);

        // Assert
        await Assert.That(predicate(new HttpRequestException())).IsTrue();
        await Assert.That(predicate(new TimeoutException())).IsTrue();
        await Assert.That(predicate(new ArgumentException())).IsFalse();
    }

    /// <summary>
    /// Verifies CreateExceptionPredicate handles empty array.
    /// </summary>
    [Test]
    public async Task CreateExceptionPredicate_EmptyTypes_UsesTransientTypes()
    {
        // Act
        var predicate = ResiliencyPolicyGenerator.CreateExceptionPredicate([]);

        // Assert
        await Assert.That(predicate(new HttpRequestException())).IsTrue();
        await Assert.That(predicate(new IOException())).IsTrue();
    }

    /// <summary>
    /// Verifies CreateExceptionPredicate handles custom types.
    /// </summary>
    [Test]
    public async Task CreateExceptionPredicate_CustomTypes_MatchesSpecifiedTypes()
    {
        // Act
        var predicate = ResiliencyPolicyGenerator.CreateExceptionPredicate([typeof(ArgumentException)]);

        // Assert
        await Assert.That(predicate(new ArgumentException())).IsTrue();
        await Assert.That(predicate(new ArgumentNullException())).IsTrue(); // Derived type
        await Assert.That(predicate(new HttpRequestException())).IsFalse();
    }
}