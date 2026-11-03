namespace LmsTokps;

internal static class Program
{
    /// <summary>%USERPROFILE%\.lmstudio\apps\bionic\server-logs</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lmstudio", "apps", "bionic", "server-logs");

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);


        // A --root on the command line always wins over the saved setting.
        string? explicitRoot = null;
        int? sleepOverride = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
                explicitRoot = a["--root=".Length..];
            else if ((a == "--root" || a == "-r") && i + 1 < args.Length)
                explicitRoot = args[i + 1];
            else if (a.StartsWith("--sleep=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(a["--sleep=".Length..], out int m))
                sleepOverride = Math.Max(0, m);
        }

        var settings = Settings.Load();
        if (explicitRoot is not null)
            settings.Root = explicitRoot;
        else if (string.IsNullOrWhiteSpace(settings.Root))
            settings.Root = DefaultRoot;
        if (sleepOverride is int sm)
            settings.AutoSleepMinutes = sm;

        var form = new WidgetForm(settings);
        var watcher = new LogWatcher(settings.Root);

        // Event-driven: the OS notifies us when a log changes (reads are
        // throttled to ~3/s), with a slow 30 s fallback poll in case events
        // are dropped. No steady-state polling while things are quiet.
        watcher.SampleReady += form.ApplySample;

        form.FormClosed += (s, e) => watcher.Dispose();

        form.Show();          // create the handle first, so BeginInvoke works
        watcher.Start();      // initial sample + start watching
        Application.Run(form);
    }
}
