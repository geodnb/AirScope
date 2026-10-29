namespace AirScope.Core;

/// <summary>Thread-safe fixed-capacity ring buffer of link samples.</summary>
public sealed class SampleBuffer
{
    private readonly LinkSample[] _buf;
    private int _start, _count;
    private readonly object _lock = new();

    public SampleBuffer(int capacity) => _buf = new LinkSample[capacity];

    public void Add(LinkSample s)
    {
        lock (_lock)
        {
            if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = s;
            else { _buf[_start] = s; _start = (_start + 1) % _buf.Length; }
        }
    }

    public void Clear() { lock (_lock) { _start = 0; _count = 0; } }

    /// <summary>Copy samples newer than <paramref name="sinceUtc"/> into <paramref name="into"/>.</summary>
    public void CopySince(DateTime sinceUtc, List<LinkSample> into)
    {
        into.Clear();
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var s = _buf[(_start + i) % _buf.Length];
                if (s.Utc >= sinceUtc) into.Add(s);
            }
        }
    }

    public LinkSample? Last
    {
        get { lock (_lock) return _count == 0 ? null : _buf[(_start + _count - 1) % _buf.Length]; }
    }
}

public readonly record struct Stats(int Count, double Min, double Max, double Avg, double StdDev)
{
    public static Stats Of(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return new Stats(0, 0, 0, 0, 0);
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var x in v) { if (x < min) min = x; if (x > max) max = x; sum += x; }
        double avg = sum / v.Count, sq = 0;
        foreach (var x in v) sq += (x - avg) * (x - avg);
        return new Stats(v.Count, min, max, avg, Math.Sqrt(sq / v.Count));
    }
}
