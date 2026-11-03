using System.Timers;

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
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
                explicitRoot = a["--root=".Length..];
            else if ((a == "--root" || a == "-r") && i + 1 < args.Length)
                explicitRoot = args[i + 1];
        }

        var settings = Settings.Load();
        if (explicitRoot is not null)
            settings.Root = explicitRoot;
        else if (string.IsNullOrWhiteSpace(settings.Root))
            settings.Root = DefaultRoot;

        var form = new WidgetForm(settings);
        var watcher = new LogWatcher(settings.Root);

        var timer = new System.Timers.Timer(1000) { AutoReset = true };
        timer.Elapsed += (s, e) =>
        {
            LogSample? sample = null;
            try { sample = watcher.Sample(); }
            catch { sample = null; }
            form.ApplySample(sample);
        };

        form.FormClosed += (s, e) =>
        {
            timer.Stop();
            timer.Dispose();
        };

        timer.Start();
        form.Show();
        Application.Run(form);
    }
}
