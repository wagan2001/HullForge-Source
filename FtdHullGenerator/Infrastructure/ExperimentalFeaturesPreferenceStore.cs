namespace FtdHullGenerator.Infrastructure;

/// <summary>Persists the machine-local exposure preference; it never enters a ship document.</summary>
public sealed class ExperimentalFeaturesPreferenceStore
{
    public ExperimentalFeaturesPreferenceStore(string? applicationDataRoot = null)
    {
        var root = applicationDataRoot ??
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        PreferencePath = Path.Combine(root, "Hull Forge", "experimental-features.txt");
    }

    public string PreferencePath { get; }

    public bool Load()
    {
        try
        {
            return Decode(File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Save(bool enabled, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, enabled ? "on" : "off");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The Experimental Features preference could not be saved: {exception.Message}";
            return false;
        }
    }

    internal static bool Decode(string? stored) =>
        string.Equals(stored?.Trim(), "on", StringComparison.OrdinalIgnoreCase);
}
