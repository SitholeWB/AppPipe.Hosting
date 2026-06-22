using System.Diagnostics;
using System.Threading;

namespace AppPipe.Hosting;

public class GatewayDiagnosticsService : IDisposable
{
    private int _activeRequests;
    private long _totalRequests;
    
    private long _spansReceived;
    private long _logsReceived;
    private long _metricsReceived;

    // Rates (calculated over the last second)
    private double _spansPerSecond;
    private double _logsPerSecond;
    private double _metricsPerSecond;

    private readonly System.Threading.Timer _rateTimer;
    private long _lastSpans;
    private long _lastLogs;
    private long _lastMetrics;

    public GatewayDiagnosticsService()
    {
        _rateTimer = new System.Threading.Timer(CalculateRates, null, 1000, 1000);
    }

    public int ActiveRequests => _activeRequests;
    public long TotalRequests => _totalRequests;
    public long SpansReceived => _spansReceived;
    public long LogsReceived => _logsReceived;
    public long MetricsReceived => _metricsReceived;
    
    public double SpansPerSecond => _spansPerSecond;
    public double LogsPerSecond => _logsPerSecond;
    public double MetricsPerSecond => _metricsPerSecond;

    public void IncrementActiveRequests()
    {
        Interlocked.Increment(ref _activeRequests);
        Interlocked.Increment(ref _totalRequests);
    }

    public void DecrementActiveRequests()
    {
        Interlocked.Decrement(ref _activeRequests);
    }

    public void AddSpans(int count)
    {
        Interlocked.Add(ref _spansReceived, count);
    }

    public void AddLogs(int count)
    {
        Interlocked.Add(ref _logsReceived, count);
    }

    public void AddMetrics(int count)
    {
        Interlocked.Add(ref _metricsReceived, count);
    }

    private void CalculateRates(object? state)
    {
        var currentSpans = Interlocked.Read(ref _spansReceived);
        var currentLogs = Interlocked.Read(ref _logsReceived);
        var currentMetrics = Interlocked.Read(ref _metricsReceived);

        _spansPerSecond = currentSpans - _lastSpans;
        _logsPerSecond = currentLogs - _lastLogs;
        _metricsPerSecond = currentMetrics - _lastMetrics;

        _lastSpans = currentSpans;
        _lastLogs = currentLogs;
        _lastMetrics = currentMetrics;
    }

    public void Dispose()
    {
        _rateTimer.Dispose();
    }
}
