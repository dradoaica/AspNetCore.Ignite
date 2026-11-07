using System.Diagnostics.Metrics;
using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteNodeMetricsTests
{
    [Fact]
    public void WithoutRunningNode_OnlyReadinessAndRebalancingAreReported()
    {
        using IgniteNodeMetrics metrics = new(new IgniteNodeState());
        Dictionary<string, List<double>> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == IgniteNodeMetrics.MeterName) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Equal([0d], measurements["node_ready"]);
        Assert.Equal([0d], measurements["node_rebalancing_caches"]);
        Assert.Equal(2, measurements.Count);

        void Record(Instrument instrument, double value)
        {
            if (!measurements.TryGetValue(instrument.Name, out var values)) measurements[instrument.Name] = values = [];

            values.Add(value);
        }
    }

    [Fact]
    public void PerCacheMetrics_CanBeTurnedOff()
    {
        using IgniteNodeMetrics metrics = new(new IgniteNodeState(), false);
        List<string> instruments = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == IgniteNodeMetrics.MeterName) instruments.Add(instrument.Name);
        };
        listener.Start();

        Assert.DoesNotContain(instruments, name => name.StartsWith("cache_", StringComparison.Ordinal));
        Assert.Contains("node_ready", instruments);
    }
}