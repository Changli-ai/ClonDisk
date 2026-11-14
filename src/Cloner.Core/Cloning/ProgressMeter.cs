namespace Cloner.Core.Cloning;

/// <summary>Calcula velocidad y tiempo restante a partir de los bytes de los últimos segundos.</summary>
internal sealed class ProgressMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time;
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private long _lastReport;

    public ProgressMeter(long total, TimeProvider time)
    {
        Total = total;
        _time = time;
        var now = time.GetTimestamp();
        _samples.Enqueue((now, 0));
        _lastReport = now;
    }

    public long Total { get; }
    public long Done { get; private set; }

    public void Advance(long bytes)
    {
        Done += bytes;
        var now = _time.GetTimestamp();
        _samples.Enqueue((now, Done));

        // Se conserva siempre una muestra anterior a la ventana como punto de partida.
        while (_samples.Count > 2 && _time.GetElapsedTime(_samples.ElementAt(1).Timestamp, now) > Window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Bytes por segundo en la ventana reciente; 0 si ha pasado menos de un segundo.</summary>
    public double BytesPerSecond
    {
        get
        {
            var (firstTime, firstBytes) = _samples.Peek();
            var elapsed = _time.GetElapsedTime(firstTime);
            return elapsed < MinimumSpan ? 0 : (Done - firstBytes) / elapsed.TotalSeconds;
        }
    }

    public TimeSpan? Remaining
    {
        get
        {
            var speed = BytesPerSecond;
            return speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, Total - Done) / speed) : null;
        }
    }

    /// <summary>Indica si ha pasado suficiente tiempo desde el último aviso de progreso.</summary>
    public bool ShouldReport()
    {
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_lastReport, now) < ReportInterval)
        {
            return false;
        }

        _lastReport = now;
        return true;
    }

    public CloneProgress Snapshot(ClonePhase phase, string? item) => new()
    {
        Phase = phase,
        BytesDone = Done,
        BytesTotal = Total,
        BytesPerSecond = BytesPerSecond,
        Remaining = Remaining,
        CurrentItem = item,
    };
}
