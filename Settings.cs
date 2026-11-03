using System.Text.Json;

namespace LmsTokps;

/// <summary>Persisted to %AppData%\lms-tokps\settings.json.</summary>
public sealed class Settings
{
    public string? Root { get; set; }
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public bool Paused { get; set; }
    public bool ShowPrompt { get; set; } = true;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lms-tokps");

    public static Settings Load()
    {
        try
        {
            string f = Path.Combine(Dir, "settings.json");
            if (File.Exists(f))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(f));
                if (s is not null) return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string f = Path.Combine(Dir, "settings.json");
            File.WriteAllText(f, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
