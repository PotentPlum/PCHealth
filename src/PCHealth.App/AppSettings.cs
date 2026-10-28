using System.IO;
using System.Text.Json;

namespace PCHealth.App;

/// <summary>
/// The user's settings (spec §6.5), kept in one local JSON file, plus the file and folder counts of the last full scan,
/// which only serve to estimate the next scan's progress (T-66). A missing file means the defaults.
/// </summary>
public sealed class AppSettings
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 90;
    public const int DefaultRetentionDays = 14;
    public const int MinArchiveMonths = 1;
    public const int MaxArchiveMonths = 120;

    private const int CurrentVersion = 1;

    /// <summary>How long items moved to quarantine from now on stay restorable.</summary>
    public int QuarantineRetentionDays { get; private set; } = DefaultRetentionDays;

    public TimeSpan QuarantineRetention => TimeSpan.FromDays(QuarantineRetentionDays);

    /// <summary>After how many months without changes a project is offered for archiving (T-53).</summary>
    public int ArchiveAfterMonths { get; private set; } = Organization.ProjectArchive.DefaultMonths;

    /// <summary>How many files the last full scan counted; null before the first one.</summary>
    public long? LastScanFiles { get; private set; }

    public long? LastScanFolders { get; private set; }

    public void RecordScan(long files, long folders)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(files);
        ArgumentOutOfRangeException.ThrowIfNegative(folders);
        (LastScanFiles, LastScanFolders) = (files, folders);
    }

    public void SetArchiveAfterMonths(int months)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(months, MinArchiveMonths);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(months, MaxArchiveMonths);
        ArchiveAfterMonths = months;
    }

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
            || days is < MinRetentionDays or > MaxRetentionDays
            || document.LastScanFiles < 0
            || document.LastScanFolders < 0
            || document.ArchiveAfterMonths is < MinArchiveMonths or > MaxArchiveMonths)
        {
            throw new InvalidDataException($"'{filePath}' has an unsupported format.");
        }

        settings.QuarantineRetentionDays = days;
        (settings.LastScanFiles, settings.LastScanFolders) = (document.LastScanFiles, document.LastScanFolders);
        settings.ArchiveAfterMonths = document.ArchiveAfterMonths ?? Organization.ProjectArchive.DefaultMonths;
        return settings;
    }

    /// <summary>Writes the settings, replacing the old file only once the new one is complete.</summary>
    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(CurrentVersion, QuarantineRetentionDays, LastScanFiles, LastScanFolders, ArchiveAfterMonths), Json));
        File.Move(temporary, filePath, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <param name="ArchiveAfterMonths">Absent in files written before T-53; the default applies then.</param>
    private sealed record Document(int Version, int? QuarantineRetentionDays, long? LastScanFiles = null, long? LastScanFolders = null, int? ArchiveAfterMonths = null);
}
