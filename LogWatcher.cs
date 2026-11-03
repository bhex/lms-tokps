using System.Globalization;
using System.Text.RegularExpressions;

namespace LmsTokps;

/// <summary>
/// One snapshot of the server's generation throughput, parsed from the newest
/// log activity.
/// </summary>
public sealed class LogSample
{
    public double? Throughput;   // headline: sustained generation t/s (running avg "tg")
    public double? Instant;      // live t/s over the last 3 s ("tg_3s")
    public int? Tokens;          // tokens generated so far in the current generation
    public double? PromptTps;    // prompt-processing t/s (most recent)
    public DateTime? Timestamp;  // time of the newest generation line seen
    public bool Generating;      // a live progress line was seen within the live window
    public string? SourceFile;
}

/// <summary>
/// Tails the LM Studio server-logs folder and reads the *live* generation
/// throughput. Prefer the per-3-second progress lines (tg / tg_3s), falling
/// back to the end-of-completion summary.
/// </summary>
public sealed class LogWatcher
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Regex ProgressRegex = new(
        @"n_gen\s*=\s*(\d+)\s*,\s*tg\s*=\s*([\d.]+)\s*t/s\s*,\s*tg_3s\s*=\s*([\d.]+)\s*t/s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TpsAtEndRegex = new(
        @"([\d.]+)\s*tokens per second",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TsRegex = new(
        @"\[(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled);

    private const int TailBytes = 512 * 1024;
    private const int LiveWindowSeconds = 5;

    private readonly string _root;

    public LogWatcher(string root) { _root = root; }
    public string Root => _root;

    // ---- event-driven updates ------------------------------------------
    // The OS tells us when a log changes (FileSystemWatcher); reads are
    // throttled to at most one per MinSampleIntervalMs, and a slow fallback
    // poll keeps things honest in case events are dropped.
    private const int MinSampleIntervalMs = 300;
    private const int FallbackIntervalMs = 30_000;

    private readonly object _gate = new();
    private bool _samplePending;
    private DateTime _lastSampleAt = DateTime.MinValue;

    private FileSystemWatcher? _fsWatcher;
    private System.Threading.Timer? _sampleTimer;
    private System.Threading.Timer? _fallbackTimer;
    private bool _started;

    /// <summary>Raised on a background thread; marshal to the UI thread.</summary>
    public event Action<LogSample?>? SampleReady;

    public void Start()
    {
        if (_started) return;
        _started = true;

        _sampleTimer = new System.Threading.Timer(_ =>
        {
            lock (_gate) _samplePending = false;
            RaiseSample();
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        TryCreateFsWatcher();

        _fallbackTimer = new System.Threading.Timer(_ =>
        {
            if (_fsWatcher is null) TryCreateFsWatcher();  // the root may have appeared
            RaiseSample();                                  // cheap: one 512 KB read / 30 s
        }, null, FallbackIntervalMs, FallbackIntervalMs);

        RaiseSample();  // initial paint
    }

    private void RaiseSample()
    {
        LogSample? sample;
        try { sample = Sample(); } catch { sample = null; }
        SampleReady?.Invoke(sample);
    }

    private void TryCreateFsWatcher()
    {
        if (_fsWatcher is not null) return;
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root)) return;
        try
        {
            _fsWatcher = new FileSystemWatcher(_root)
            {
                Filter = "*.log",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _fsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite
                | System.IO.NotifyFilters.Size
                | System.IO.NotifyFilters.FileName;
            _fsWatcher.Changed += OnFsEvent;
            _fsWatcher.Created += OnFsEvent;
        }
        catch { _fsWatcher = null; }   // fallback poll still covers us
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ScheduleSample();

    private void ScheduleSample()
    {
        if (_sampleTimer is null) return;
        int delayMs;
        lock (_gate)
        {
            if (_samplePending) return;          // one read is already queued
            double since = (DateTime.Now - _lastSampleAt).TotalMilliseconds;
            delayMs = (int)Math.Max(0, MinSampleIntervalMs - since);
            _samplePending = true;
            _lastSampleAt = DateTime.Now;
        }
        _sampleTimer.Change(delayMs, System.Threading.Timeout.Infinite);
    }

    public void Dispose()
    {
        _fsWatcher?.Dispose();
        _sampleTimer?.Dispose();
        _fallbackTimer?.Dispose();
    }

    public LogSample? Sample()
    {
        string? file = NewestLog();
        if (file is null) return null;
        string text;
        try { text = ReadTail(file); } catch { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;

        double? throughput = null;    // tg (running avg)
        double? instant = null;       // tg_3s (live)
        int? tokens = null;           // n_gen
        double? prompt = null;        // prompt eval t/s
        double? completionGen = null; // "eval time ... tokens per second"
        DateTime? progressTs = null;
        DateTime? completionTs = null;

        string[] lines = text.Split('\n');
        int scanned = 0;
        for (int i = lines.Length - 1; i >= 0 && scanned < 400; i--)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            scanned++;

            if (line.Contains("n_gen", StringComparison.Ordinal))
            {
                if (ProgressRegex.Match(line) is { Success: true } m)
                {
                    if (int.TryParse(m.Groups[1].Value, out int ng)) tokens ??= ng;
                    if (double.TryParse(m.Groups[2].Value, Inv, out double tg)) throughput ??= tg;
                    if (double.TryParse(m.Groups[3].Value, Inv, out double t3)) instant ??= t3;
                    if (Ts(line) is DateTime t) progressTs ??= t;
                }
            }
            else if (line.Contains("prompt eval time", StringComparison.OrdinalIgnoreCase))
            {
                if (TpsAtEndRegex.Match(line) is { Success: true } m &&
                    double.TryParse(m.Groups[1].Value, Inv, out double p)) prompt ??= p;
            }
            else if (line.Contains("eval time", StringComparison.OrdinalIgnoreCase))
            {
                if (TpsAtEndRegex.Match(line) is { Success: true } m &&
                    double.TryParse(m.Groups[1].Value, Inv, out double g)) completionGen ??= g;
                if (Ts(line) is DateTime t) completionTs ??= t;
            }

            if (throughput is not null && prompt is not null &&
                (completionGen is not null || progressTs is not null))
                break;
        }

        double? headline = throughput ?? completionGen;
        if (headline is null && prompt is null) return null;

        DateTime? latestTs = LatestOf(progressTs, completionTs);
        bool generating = progressTs is DateTime pt &&
            (DateTime.Now - pt).TotalSeconds <= LiveWindowSeconds;

        return new LogSample
        {
            Throughput = headline,
            Instant = instant,
            Tokens = tokens,
            PromptTps = prompt,
            Timestamp = latestTs,
            Generating = generating,
            SourceFile = file,
        };
    }

    // Log timestamps are written in the machine's local time — parse them as
    // local (AssumeLocal) so display and the LIVE-window check stay correct.
    // (Parsing as None yields Kind.Utc, and a later ToLocalTime() would then
    // wrongly add the UTC offset — e.g. 18:56 shown as 20:56.)
    private DateTime? Ts(string line)
        => TsRegex.Match(line) is { Success: true } m &&
           DateTime.TryParse(m.Groups[1].Value, Inv, DateTimeStyles.AssumeLocal, out DateTime t)
            ? t : null;

    private static DateTime? LatestOf(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value > b.Value ? a : b;
    }

    private string? NewestLog()
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root)) return null;
        try
        {
            return Directory
                .EnumerateFiles(_root, "*.log", SearchOption.AllDirectories)
                .Where(static f =>
                {
                    try { return new FileInfo(f).Length > 0; } catch { return false; }
                })
                .OrderByDescending(static f =>
                {
                    try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; }
                })
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private string ReadTail(string file)
    {
        long length = new FileInfo(file).Length;
        int toRead = (int)Math.Min(TailBytes, length);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(-toRead, SeekOrigin.End);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
