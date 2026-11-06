using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

var duration = int.Parse(args[0]);
var warmup = int.Parse(args[1]);
var documents = new[] { 1, 4, 16, 64 }
    .Select(size => JsonSerializer.SerializeToUtf8Bytes(
        Enumerable.Range(1, size).Select(i => new { id = i, qty = i * 2 })))
    .ToArray();

var expected = 2 * (1 + 10 + 136 + 2080);
Run(warmup, false);
Console.Error.WriteLine("MEASURE");
var result = Run(duration, true);
Console.WriteLine(JsonSerializer.Serialize(result));

object Run(int seconds, bool measure)
{
    var ticks = new List<long>();
    long batches = 0;
    long failures = 0;
    using var process = Process.GetCurrentProcess();
    var cpu = process.TotalProcessorTime;
    var allocated = GC.GetTotalAllocatedBytes(true);
    var paused = GC.GetTotalPauseDuration();
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        var start = Stopwatch.GetTimestamp();
        for (var j = 0; j < 64; j++)
        {
            var sum = 0;
            foreach (var bytes in documents)
            {
                using var document = JsonDocument.Parse(bytes);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    sum += item.GetProperty("qty").GetInt32();
                }
            }

            if (sum != expected)
            {
                failures++;
            }
        }

        batches++;
        if (measure)
        {
            ticks.Add(Stopwatch.GetTimestamp() - start);
        }
    }

    clock.Stop();
    var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
    var pauseMs = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds;
    var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
    ticks.Sort();
    return new
    {
        serverGC = GCSettings.IsServerGC,
        processorCount = Environment.ProcessorCount,
        runtime = Environment.Version.ToString(),
        batches,
        failures,
        seconds = clock.Elapsed.TotalSeconds,
        batchesPerSecond = batches / clock.Elapsed.TotalSeconds,
        allocatedBytesPerBatch = allocatedBytes / (double)batches,
        pauseMs,
        cpuMs,
        peakWorkingSet = process.PeakWorkingSet64,
        p99BatchMs = measure ? ticks[(int)Math.Ceiling(0.99 * ticks.Count) - 1] * 1000.0 / Stopwatch.Frequency : 0
    };
}
