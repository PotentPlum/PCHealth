using System.IO;
using System.Text.Json;

namespace PCHealth.App;

/// <summary>The user's settings (spec §6.5), kept in one local JSON file. A missing file means the defaults.</summary>
public sealed class AppSettings
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 90;
    public const int DefaultRetentionDays = 14;

    private const int CurrentVersion = 1;

    /// <summary>How long items moved to quarantine from now on stay restorable.</summary>
    public int QuarantineRetentionDays { get; private set; } = DefaultRetentionDays;

    public TimeSpan QuarantineRetention => TimeSpan.FromDays(QuarantineRetentionDays);

    public void SetQuarantineRetentionDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, MinRetentionDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, MaxRetentionDays);
        QuarantineRetentionDays = days;
    }

    /// <exception cref="InvalidDataException">The file exists but is not valid settings.</exception>
    public static AppSettings Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var settings = new AppSettings();
        if (!File.Exists(filePath))
        {
            return settings;
        }

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{filePath}' is not a valid settings file.", ex);
        }

        if (document?.Version != CurrentVersion
            || document.QuarantineRetentionDays is not { } days
            || days is < MinRetentionDays or > MaxRetentionDays)
        {
            throw new InvalidDataException($"'{filePath}' has an unsupported format.");
        }

        settings.QuarantineRetentionDays = days;
        return settings;
    }

    /// <summary>Writes the settings, replacing the old file only once the new one is complete.</summary>
    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(CurrentVersion, QuarantineRetentionDays), Json));
        File.Move(temporary, filePath, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Document(int Version, int? QuarantineRetentionDays);
}
