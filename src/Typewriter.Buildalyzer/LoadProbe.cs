using System.Collections.Concurrent;
using System.Globalization;

namespace Typewriter.Buildalyzer;

// Opt-in diagnostic probe that attributes MSBuild load wall clock time by stage.
// Enabled with TYPEWRITER_TRACE_LOAD=1; inert and allocation-free otherwise.
internal static class LoadProbe
{
    // The buffer is bounded because the probe also runs inside long-lived hosts (Visual Studio),
    // where the process may generate events for hours and ProcessExit handlers are time-boxed and
    // frequently skipped altogether. An unbounded queue would grow without limit and never be
    // drained there. Keeping the most recent window is the useful behaviour for a load probe: the
    // interesting events are always the ones from the generation being investigated.
    private const int MaxBufferedEvents = 4096;

    private static readonly ConcurrentQueue<string> Events = new();

    private static long _droppedEvents;

    static LoadProbe()
    {
        if (Enabled)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Dump();
        }
    }

    public static bool Enabled { get; } =
        string.Equals(
            a: Environment.GetEnvironmentVariable(variable: "TYPEWRITER_TRACE_LOAD"),
            b: "1",
            comparisonType: StringComparison.Ordinal);

    public static void Report(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Events.Enqueue(item: message);

        // Trimming after the enqueue keeps this lock-free. Concurrent reporters can briefly push
        // the queue a little past the bound before it settles back, which is acceptable for a
        // diagnostic buffer.
        while (Events.Count > MaxBufferedEvents && Events.TryDequeue(result: out _))
        {
            _ = Interlocked.Increment(location: ref _droppedEvents);
        }
    }

    public static void ReportTiming(string stage, string name, TimeSpan elapsed) =>
        Report(
            message: string.Format(
                provider: CultureInfo.InvariantCulture,
                format: "TWLOAD: {0,-16} {1,-28} {2,9:F1} ms",
                arg0: stage,
                arg1: name,
                arg2: elapsed.TotalMilliseconds));

    private static void Dump()
    {
        var dropped = Interlocked.Read(location: ref _droppedEvents);
        if (dropped > 0)
        {
            // Reported so a truncated trace is never mistaken for a complete one.
            Console.Error.WriteLine(
                value: string.Format(
                    provider: CultureInfo.InvariantCulture,
                    format: "TWLOAD: {0} earlier event(s) dropped; buffer is capped at {1}.",
                    arg0: dropped,
                    arg1: MaxBufferedEvents));
        }

        while (Events.TryDequeue(result: out var line))
        {
            Console.Error.WriteLine(value: line);
        }
    }
}
