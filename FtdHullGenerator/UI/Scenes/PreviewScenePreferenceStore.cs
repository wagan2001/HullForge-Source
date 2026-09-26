using System.Text.Json;

namespace FtdHullGenerator.UI.Scenes;

/// <summary>
/// Machine-local scene preferences. They are deliberately outside project serialization so
/// sharing a ship never changes its geometry revision or another player's presentation.
/// </summary>
internal sealed class PreviewScenePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public PreviewScenePreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hull Forge",
            "preview-scene.json");
    }

    public PreviewSceneSettings LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_path))
                return PreviewSceneSettings.Grid;
            var settings = JsonSerializer.Deserialize<PreviewSceneSettings>(File.ReadAllText(_path), JsonOptions);
            if (settings is null)
                return PreviewSceneSettings.Grid;
            settings.Validate();
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return PreviewSceneSettings.Grid;
        }
    }

    public void Save(PreviewSceneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Scene preference path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }
}
