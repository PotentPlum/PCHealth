using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCHealth.Core;

namespace PCHealth.Policy.Recommendations;

/// <summary>A recommendation the user declined, with what its evidence looked like at the time.</summary>
public sealed record Decline(string Key, string Fingerprint, long? ReclaimableBytes, DateTimeOffset DeclinedUtc);

/// <summary>
/// Recommendations the user declined (spec §4, §6.5). A declined recommendation stays hidden unless materially new
/// evidence changes it: different evidence (other paths, other content hashes, other related registrations), or
/// an estimated benefit that has at least doubled. Growth alone below that does not bring it back.
/// Kept as local JSON, saved atomically.
/// </summary>
public sealed class DeclinedRecommendations
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<string, Decline> declines = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Decline> Entries => declines.Values;

    /// <summary>What identifies the evidence, leaving out measurements that change on every scan.</summary>
    public static string Fingerprint(Recommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var parts = recommendation.Evidence.Select(e => e switch
        {
            PathEvidence p => $"path|{p.Path}",
            FileContentEvidence f => $"content|{f.Path}|{f.ContentHash}",
            RegistrationEvidence r => $"registration|{r.Source}|{r.Name}",
            InaccessibleEvidence i => $"unreadable|{i.Path}",
            _ => null,
        }).OfType<string>().Order(StringComparer.Ordinal);
        var text = $"{recommendation.Category}\n{recommendation.Disposition}\n{string.Join('\n', parts)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public Decline Decline(Recommendation recommendation, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var decline = new Decline(recommendation.Key, Fingerprint(recommendation), recommendation.Impact.PotentiallyReclaimable?.Bytes, nowUtc);
        declines[decline.Key] = decline;
        return decline;
    }

    public bool IsDeclined(Recommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        if (!declines.TryGetValue(recommendation.Key, out var decline) || decline.Fingerprint != Fingerprint(recommendation))
        {
            return false;
        }

        var now = recommendation.Impact.PotentiallyReclaimable?.Bytes;
        return !(now is { } bytes && decline.ReclaimableBytes is { } then && bytes >= 2 * Math.Max(then, 1));
    }

    public bool Remove(string key) => declines.Remove(key);

    public static DeclinedRecommendations Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var list = new DeclinedRecommendations();
        if (!File.Exists(filePath))
        {
            return list;
        }

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{filePath}' is not a readable list of declined recommendations.", ex);
        }

        if (document is null || document.Version != CurrentVersion || document.Declines is null
            || document.Declines.Any(d => d is null || string.IsNullOrWhiteSpace(d.Key) || string.IsNullOrWhiteSpace(d.Fingerprint)))
        {
            throw new InvalidDataException($"'{filePath}' has an unsupported format.");
        }

        foreach (var decline in document.Declines)
        {
            list.declines[decline.Key] = decline;
        }

        return list;
    }

    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(CurrentVersion, declines.Values.ToArray()), Json));
        File.Move(temporary, filePath, overwrite: true);
    }

    private sealed record Document(int Version, IReadOnlyList<Decline>? Declines);
}
