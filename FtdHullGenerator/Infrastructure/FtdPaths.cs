using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace FtdHullGenerator.Infrastructure;

public sealed record FtdPaths(
    string DocumentsRoot,
    string ContentRoot,
    string ActiveProfile,
    string ConstructsDirectory,
    IReadOnlyList<string> Profiles,
    string? ContentFolderOverride)
{
    public static FtdPaths Discover()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var contentRoot = Path.Combine(documents, "From The Depths");
        var optionsPath = Path.Combine(contentRoot, "Options.dat");
        var profile = "Default";
        string? overridePath = null;

        if (File.Exists(optionsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsPath));
                if (document.RootElement.TryGetProperty("DefaultProfile", out var profileElement) &&
                    profileElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(profileElement.GetString()))
                {
                    profile = profileElement.GetString()!;
                }

                if (document.RootElement.TryGetProperty("ContentFolderOverride", out var overrideElement) &&
                    overrideElement.ValueKind == JsonValueKind.String)
                {
                    var candidate = overrideElement.GetString();
                    if (!string.IsNullOrWhiteSpace(candidate) && !string.Equals(candidate, "NA", StringComparison.OrdinalIgnoreCase))
                        overridePath = candidate;
                }
            }
            catch (JsonException)
            {
                // The game can recover from a malformed options file; retain the conventional layout.
            }
        }

        var profileRoot = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(contentRoot, "Player Profiles")
            : Path.GetFullPath(overridePath!);
        var profiles = Directory.Exists(profileRoot)
            ? Directory.EnumerateDirectories(profileRoot).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();

        if (!profiles.Contains(profile, StringComparer.OrdinalIgnoreCase) && profiles.Length > 0)
            profile = profiles[0];

        return new FtdPaths(
            documents,
            contentRoot,
            profile,
            Path.Combine(profileRoot, profile, "Constructables"),
            profiles,
            overridePath);
    }

    public FtdPaths WithProfile(string profile) => this with
    {
        ActiveProfile = profile,
        ConstructsDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(ContentFolderOverride) ? Path.Combine(ContentRoot, "Player Profiles") : Path.GetFullPath(ContentFolderOverride),
            profile,
            "Constructables"),
    };
}

public static class FtdInstallationLocator
{
    public static string? FindInstalledGame()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "From The Depths"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "From The Depths"),
        };

        return candidates.FirstOrDefault(IsGameDirectory);
    }

    public static bool IsGameDirectory([NotNullWhen(true)] string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (File.Exists(Path.Combine(path, "From_The_Depths.exe")) || File.Exists(Path.Combine(path, "From The Depths.exe"))) &&
        Directory.Exists(Path.Combine(path, "From_The_Depths_Data", "StreamingAssets"));

    public static string GetStreamingAssets(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "From_The_Depths_Data", "StreamingAssets");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"From The Depths StreamingAssets folder was not found under '{gameDirectory}'.");
        return path;
    }
}
