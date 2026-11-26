// =============================================================================
// <copyright file="MeasurementRecorder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;
using System.Globalization;

namespace Bifrost.Tests.OpenTelemetry;

/// <summary>One measurement seen by a <see cref="MeasurementRecorder"/>, with every tag it carried.</summary>
/// <param name="Instrument">The instrument name.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Tags">Every tag of the measurement, with its value as invariant text.</param>
internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyList<KeyValuePair<string, string?>> Tags)
{
    /// <summary>Gets the text value of the tag with <paramref name="key"/>, or <see langword="null"/>.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns>The tag value.</returns>
    public string? Tag(string key) => Tags.FirstOrDefault(t => t.Key == key).Value;
}

/// <summary>
/// Records every measurement of every instrument of one <see cref="Meter"/> instance, with all tags.
/// </summary>
/// <remarks>
/// Instruments are matched by meter instance, not by name, so meters with the same name in parallel
/// tests never mix in. Observable instruments report only when <see cref="Collect"/> runs.
/// </remarks>
internal sealed class MeasurementRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Lock _sync = new();
    private readonly List<RecordedMeasurement> _measurements = [];

    /// <summary>Starts recording the instruments of <paramref name="meter"/>.</summary>
    /// <param name="meter">The meter instance to observe.</param>
    public MeasurementRecorder(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
        _listener.Start();
    }

    /// <summary>Asks every observable instrument for its current values.</summary>
    public void Collect() => _listener.RecordObservableInstruments();

    /// <summary>Removes every measurement recorded so far.</summary>
    public void Clear()
    {
        lock (_sync) _measurements.Clear();
    }

    /// <summary>Takes a snapshot of the measurements recorded so far.</summary>
    /// <returns>The measurements in record order.</returns>
    public IReadOnlyList<RecordedMeasurement> Snapshot()
    {
        lock (_sync) return [.. _measurements];
    }

    /// <summary>Gets the measurements of one instrument.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>The instrument's measurements in record order.</returns>
    public IReadOnlyList<RecordedMeasurement> Of(string instrument) => [.. Snapshot().Where(m => m.Instrument == instrument)];

    /// <summary>Gets the sum of the values of one instrument, optionally for one tag value.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <param name="tagKey">The tag key to match, or <see langword="null"/> for all measurements.</param>
    /// <param name="tagValue">The tag value to match.</param>
    /// <returns>The sum.</returns>
    public double Sum(string instrument, string? tagKey = null, string? tagValue = null)
        => Of(instrument).Where(m => tagKey is null || m.Tag(tagKey) == tagValue).Sum(m => m.Value);

    /// <summary>Gets the last value of one observable instrument, optionally for one tag value.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <param name="tagKey">The tag key to match, or <see langword="null"/> for any measurement.</param>
    /// <param name="tagValue">The tag value to match.</param>
    /// <returns>The last value, or <see langword="null"/> when none was recorded.</returns>
    public double? Last(string instrument, string? tagKey = null, string? tagValue = null)
        => Of(instrument).LastOrDefault(m => tagKey is null || m.Tag(tagKey) == tagValue)?.Value;

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();

    private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        var copied = new List<KeyValuePair<string, string?>>(tags.Length);
        foreach (var tag in tags)
            copied.Add(new(tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));

        var measurement = new RecordedMeasurement(instrument.Name, value.ToDouble(CultureInfo.InvariantCulture), copied);
        lock (_sync) _measurements.Add(measurement);
    }
}
