namespace FtdHullGenerator.Infrastructure;

/// <summary>
/// Turns a name the user typed into one the file system will accept. Kept apart from the
/// editor so it carries no window with it: naming a file is not a UI concern, and the
/// rule is worth testing on its own.
/// </summary>
public static class BlueprintNaming
{
    /// <summary>
    /// The characters a Windows file name may not contain. Stated here rather than taken
    /// from <see cref="Path.GetInvalidFileNameChars" /> because that set is whatever the
    /// running platform says — on Linux it is only the separator and NUL, which would let
    /// a colon through. A blueprint is always written into a Windows game directory, so
    /// the Windows rule is the one that matters wherever the check happens to run.
    /// </summary>
    private static readonly char[] InvalidCharacters = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Strips anything a file name may not carry, then trims the trailing dots and spaces
    /// that Windows drops silently — a name ending in one would otherwise be written to a
    /// path the user did not ask for. A name left with nothing usable comes back empty,
    /// which the caller reads as "fall back to the generated name" rather than as a name.
    /// </summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var cleaned = new string(name
            .Where(character => !char.IsControl(character) &&
                                !InvalidCharacters.Contains(character) &&
                                !Path.GetInvalidFileNameChars().Contains(character))
            .ToArray());
        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        cleaned = string.Join("_", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0)
            return string.Empty;

        // Windows reserves DOS device stems even when an extension is present. Prefixing
        // keeps the user's intent legible while guaranteeing a normal file path.
        var stem = cleaned.Split('.', 2)[0].TrimEnd('.', ' ');
        return ReservedDeviceNames.Contains(stem) ? $"_{cleaned}" : cleaned;
    }
}
