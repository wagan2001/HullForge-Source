using System.Globalization;

namespace FtdHullGenerator.UI.Workspace;

/// <summary>Machine-local workspace chrome; never part of a shareable ship document.</summary>
public sealed record WorkspacePresentationPreferences(double DockedHeight)
{
    public const double MinimumDockedHeight = 150;
    public const double MaximumDockedHeight = 600;
    public const double DefaultDockedHeight = 240;

    public WorkspacePresentationPreferences Normalize() => this with
    {
        DockedHeight = double.IsFinite(DockedHeight)
            ? Math.Clamp(DockedHeight, MinimumDockedHeight, MaximumDockedHeight)
            : DefaultDockedHeight,
    };
}

public sealed class WorkspacePresentationPreferencesStore
{
    private readonly string _path;

    public WorkspacePresentationPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static WorkspacePresentationPreferencesStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hull Forge", "workspace-height.txt"));

    public WorkspacePresentationPreferences Load()
    {
        try
        {
            if (File.Exists(_path) && double.TryParse(File.ReadAllText(_path),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                return new WorkspacePresentationPreferences(height).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

        return new WorkspacePresentationPreferences(WorkspacePresentationPreferences.DefaultDockedHeight);
    }

    public void Save(WorkspacePresentationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, preferences.Normalize().DockedHeight.ToString("R", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
