using FocusGarden.Timing;

var tests = new (string Name, Action Run)[]
{
    ("short session completes once", () =>
    {
        var clock = new FocusClock(3);
        Equal(0, clock.ElapsedSeconds);
        Equal(3, clock.RemainingSeconds);
        Equal(false, clock.IsComplete);
        Equal(1, clock.Advance(1));
        Equal(2, clock.Advance(2));
        Equal(3, clock.ElapsedSeconds);
        Equal(0, clock.RemainingSeconds);
        Equal(true, clock.IsComplete);
        Equal(0, clock.Advance(1));
        Equal(3, clock.ElapsedSeconds);
    }),
    ("fractional ticks accumulate without early credit", () =>
    {
        var clock = new FocusClock(10);
        for (int index = 0; index < 9; index++) Equal(0, clock.Advance(0.1));
        Equal(1, clock.Advance(0.1));
        Equal(1, clock.ElapsedSeconds);
        Equal(0, clock.Advance(0.25));
        Equal(1, clock.Advance(0.75));
        Equal(2, clock.ElapsedSeconds);
    }),
    ("near boundary does not round up", () =>
    {
        var clock = new FocusClock(3);
        Equal(0, clock.Advance(0.999999999));
        Equal(0, clock.ElapsedSeconds);
    }),
    ("completion caps credited seconds", () =>
    {
        var clock = new FocusClock(2);
        Equal(2, clock.Advance(5));
        Equal(2, clock.ElapsedSeconds);
        Equal(0, clock.RemainingSeconds);
        Equal(false, clock.GapDetected);
    }),
    ("gap rejects entire interval and cannot resume", () =>
    {
        var clock = new FocusClock(100);
        Equal(2, clock.Advance(2.5));
        Equal(0, clock.Advance(5.001));
        Equal(true, clock.GapDetected);
        Equal(2, clock.ElapsedSeconds);
        Equal(98, clock.RemainingSeconds);
        Equal(0, clock.Advance(1));
        Equal(2, clock.ElapsedSeconds);
        Equal(false, clock.IsComplete);
    }),
    ("sleep never becomes credited focus", () =>
    {
        var clock = new FocusClock(1500);
        Equal(1, clock.Advance(1));
        Equal(0, clock.Advance(7200));
        Equal(1, clock.ElapsedSeconds);
        Equal(1499, clock.RemainingSeconds);
        Equal(true, clock.GapDetected);
        for (int index = 0; index < 100; index++) Equal(0, clock.Advance(1));
        Equal(1, clock.ElapsedSeconds);
    }),
    ("invalid input throws without changing state", () =>
    {
        var clock = new FocusClock(10);
        foreach (double delta in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Throws<ArgumentException>(() => clock.Advance(delta));
        Equal(0, clock.ElapsedSeconds);
        Equal(false, clock.GapDetected);
        Equal(0, clock.Advance(0));
        Equal(1, clock.Advance(1));
        Throws<ArgumentException>(() => new FocusClock(0));
        Throws<ArgumentException>(() => new FocusClock(-1));
    }),
    ("wall clock and execution speed do not affect progress", () =>
    {
        var first = new FocusClock(300);
        var second = new FocusClock(300);
        for (int index = 0; index < 1000; index++) first.Advance(0.1);
        for (int index = 0; index < 100; index++) second.Advance(1);
        Equal(100, first.ElapsedSeconds);
        Equal(first.ElapsedSeconds, second.ElapsedSeconds);
        Equal(first.RemainingSeconds, second.RemainingSeconds);
        Equal(false, first.GapDetected);
    })
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"Timing tests: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
