using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using System.Diagnostics.Metrics;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteNodeMetricsTests
{
    [Fact]
    public void WithoutRunningNode_OnlyReadinessIsReported()
    {
        using IgniteNodeMetrics metrics = new(new IgniteNodeState());
        Dictionary<string, List<double>> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == IgniteNodeMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Equal([0d,], measurements["node_ready"]);
        Assert.Single(measurements);

        void Record(Instrument instrument, double value)
        {
            if (!measurements.TryGetValue(instrument.Name, out var values))
            {
                measurements[instrument.Name] = values = [];
            }

            values.Add(value);
        }
    }
}
