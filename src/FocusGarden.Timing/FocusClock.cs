namespace FocusGarden.Timing;

/// <summary>
/// Counts only supplied monotonic intervals. The caller owns its Stopwatch and
/// must end the session when GapDetected becomes true.
/// </summary>
public sealed class FocusClock
{
    private const double MaximumIntervalSeconds = 5.0;
    private readonly int _plannedSeconds;
    private double _accumulatedSeconds;
    private double _sumCompensation;

    public FocusClock(int plannedSeconds)
    {
        if (plannedSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(plannedSeconds), "A session must last at least one second.");
        _plannedSeconds = plannedSeconds;
    }

    public int ElapsedSeconds { get; private set; }
    public int RemainingSeconds => _plannedSeconds - ElapsedSeconds;
    public bool IsComplete => ElapsedSeconds == _plannedSeconds;
    public bool GapDetected { get; private set; }

    /// <summary>
    /// Returns newly credited whole seconds. An interval greater than five
    /// seconds latches a gap without crediting any part of that interval.
    /// Subsequent intervals cannot resume this instance.
    /// </summary>
    public int Advance(double monotonicSecondsDelta)
    {
        if (!double.IsFinite(monotonicSecondsDelta) || monotonicSecondsDelta < 0)
            throw new ArgumentException("The monotonic interval must be finite and nonnegative.", nameof(monotonicSecondsDelta));

        if (monotonicSecondsDelta > MaximumIntervalSeconds)
        {
            GapDetected = true;
            return 0;
        }

        if (GapDetected || IsComplete)
            return 0;

        // Compensated summation avoids losing repeated fractional timer ticks,
        // such as ten 0.1-second intervals, without counting time early.
        double adjusted = monotonicSecondsDelta - _sumCompensation;
        double next = _accumulatedSeconds + adjusted;
        _sumCompensation = (next - _accumulatedSeconds) - adjusted;
        _accumulatedSeconds = next;

        int previous = ElapsedSeconds;
        ElapsedSeconds = _accumulatedSeconds >= _plannedSeconds
            ? _plannedSeconds
            : (int)Math.Floor(_accumulatedSeconds);
        return ElapsedSeconds - previous;
    }
}
