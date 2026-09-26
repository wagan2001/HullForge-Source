using System.Text.Json;

namespace FtdHullGenerator.Serialization.Projects.Migrations;

/// <summary>
/// A named, deterministic conversion from one persisted sidecar format into design intent.
/// Migrations are pure: they read a parsed payload and return a document, never touch the disk.
/// </summary>
internal interface IProjectMigration
{
    /// <summary>A stable name. It is recorded in diagnostics and asserted by tests, never localized.</summary>
    string Name { get; }

    /// <summary>The sidecar format version this migration reads.</summary>
    int SourceFormatVersion { get; }

    bool CanMigrate(string format, int formatVersion);

    ProjectDocumentResult Migrate(JsonElement root);
}
