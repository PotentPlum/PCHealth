using PCHealth.Policy.Catalog;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Token resolution only; no real user folder is scanned or changed.</summary>
public class WindowsKnownLocationsTests
{
    private readonly WindowsKnownLocations locations = new();

    [Theory]
    [InlineData("Temp")]
    [InlineData("LocalAppData")]
    [InlineData("RoamingAppData")]
    [InlineData("ProgramData")]
    public void Tokens_resolve_to_existing_canonical_folders(string token)
    {
        var path = locations.Resolve(token);

        Assert.NotNull(path);
        Assert.True(Directory.Exists(path));
        Assert.DoesNotContain("~", path);
        Assert.True(Path.IsPathFullyQualified(path));
    }

    [Fact]
    public void Unknown_tokens_resolve_to_nothing()
    {
        Assert.Null(locations.Resolve("Nonsense"));
    }

    [Fact]
    public void Every_token_in_the_shipped_catalog_resolves()
    {
        var tokens = CacheCatalog.LoadDefault().Rules.Select(r => r.Location.Split('/')[0][1..^1]).Distinct();

        Assert.All(tokens, t => Assert.NotNull(locations.Resolve(t)));
    }

    [Fact]
    public void Every_rule_location_forms_a_full_unprotected_path_on_this_machine()
    {
        var policy = WindowsProtectedLocations.CreateDefaultPolicy();

        Assert.All(CacheCatalog.LoadDefault().Rules, rule =>
        {
            var segments = rule.Location.Split('/');
            var root = locations.Resolve(segments[0][1..^1]);
            Assert.NotNull(root);
            var rest = segments.Skip(1).Select(s => s == "*" ? "Default" : s).ToArray();
            Assert.All(rest, s => Assert.Equal(-1, s.IndexOfAny(Path.GetInvalidFileNameChars())));
            var path = Path.Combine([root, .. rest]);

            Assert.True(Path.IsPathFullyQualified(path), rule.Id);
            Assert.Equal(path, Path.GetFullPath(path));
            Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
            Assert.False(policy.Check(path).IsBlocked, $"{rule.Id}: {path}");
        });
    }

    [Fact]
    public void Cache_locations_under_the_user_profile_are_not_protected_by_default()
    {
        var policy = WindowsProtectedLocations.CreateDefaultPolicy();

        Assert.False(policy.Check(locations.Resolve("Temp")!).IsBlocked);
        Assert.False(policy.Check(Path.Combine(locations.Resolve("LocalAppData")!, "CrashDumps")).IsBlocked);
    }
}
