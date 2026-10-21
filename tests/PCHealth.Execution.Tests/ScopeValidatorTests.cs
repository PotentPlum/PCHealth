namespace PCHealth.Execution.Tests;

/// <summary>Scope logic against a stand-in resolver; real links are tested on Windows.</summary>
public class ScopeValidatorTests
{
    private static readonly string Scope = Rooted("scope");

    [Fact]
    public void A_target_inside_the_scope_is_allowed_with_its_final_path()
    {
        var resolver = new FakeResolver()
            .Directory(Scope)
            .File(Rooted("scope", "a.txt"));

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "a.txt"), Scope);

        Assert.True(decision.IsAllowed, decision.Message);
        Assert.Equal(Rooted("scope", "a.txt"), decision.FinalPath);
        Assert.Equal("", decision.Message);
    }

    [Fact]
    public void The_scope_root_itself_is_inside_the_scope()
    {
        var resolver = new FakeResolver().Directory(Scope);

        Assert.True(new ScopeValidator(resolver).Check(Scope, Scope).IsAllowed);
    }

    [Fact]
    public void A_target_that_really_lives_outside_is_rejected()
    {
        // e.g. scope/junction/file.txt where "junction" points outside: the resolver reports the real location.
        var resolver = new FakeResolver()
            .Directory(Scope)
            .File(Rooted("scope", "junction", "file.txt"), finalPath: Rooted("outside", "file.txt"));

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "junction", "file.txt"), Scope);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ScopeRejectionReason.OutsideScope, decision.Reason);
        Assert.Null(decision.FinalPath);
        Assert.Contains(Rooted("outside", "file.txt"), decision.Message);
    }

    [Fact]
    public void A_sibling_sharing_the_scope_name_as_a_prefix_is_outside()
    {
        var resolver = new FakeResolver()
            .Directory(Scope)
            .File(Rooted("scope-other", "a.txt"));

        Assert.Equal(ScopeRejectionReason.OutsideScope, new ScopeValidator(resolver).Check(Rooted("scope-other", "a.txt"), Scope).Reason);
    }

    [Fact]
    public void A_link_inside_the_scope_that_points_outside_is_rejected()
    {
        var resolver = new FakeResolver()
            .Directory(Scope)
            .Link(Rooted("scope", "link"), destination: Rooted("outside"));

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "link"), Scope);

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, decision.Reason);
        Assert.Contains(Rooted("outside"), decision.Message);
    }

    [Fact]
    public void A_link_whose_destination_cannot_be_resolved_is_rejected()
    {
        var resolver = new FakeResolver()
            .Directory(Scope)
            .Link(Rooted("scope", "dangling"), destination: null);

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "dangling"), Scope);

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, decision.Reason);
        Assert.Equal(PathRejectionReason.NotFound, decision.PathRejection);
    }

    [Fact]
    public void A_link_that_stays_inside_the_scope_is_allowed()
    {
        var resolver = new FakeResolver()
            .Directory(Scope)
            .Link(Rooted("scope", "link"), destination: Rooted("scope", "real"));

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "link"), Scope);

        Assert.True(decision.IsAllowed, decision.Message);
        Assert.Equal(Rooted("scope", "link"), decision.FinalPath);
    }

    [Fact]
    public void An_unresolvable_target_is_rejected_with_the_resolver_reason()
    {
        var resolver = new FakeResolver().Directory(Scope);

        var decision = new ScopeValidator(resolver).Check(Rooted("scope", "missing"), Scope);

        Assert.Equal(ScopeRejectionReason.TargetUnresolvable, decision.Reason);
        Assert.Equal(PathRejectionReason.NotFound, decision.PathRejection);
    }

    [Fact]
    public void A_scope_that_is_missing_or_not_a_folder_rejects_everything()
    {
        var resolver = new FakeResolver().File(Rooted("file-scope")).File(Rooted("file-scope", "x"));

        Assert.Equal(ScopeRejectionReason.ScopeUnresolvable, new ScopeValidator(resolver).Check(Rooted("file-scope", "x"), Rooted("file-scope")).Reason);
        Assert.Equal(ScopeRejectionReason.ScopeUnresolvable, new ScopeValidator(resolver).Check(Rooted("nowhere", "x"), Rooted("nowhere")).Reason);
    }

    [Fact]
    public void A_scope_reached_through_a_link_uses_its_real_location()
    {
        var resolver = new FakeResolver()
            .Directory(Rooted("alias"), finalPath: Rooted("real"))
            .File(Rooted("real", "a.txt"));

        Assert.True(new ScopeValidator(resolver).Check(Rooted("real", "a.txt"), Rooted("alias")).IsAllowed);
    }

    private static string Rooted(params string[] parts) => Path.Combine([Path.GetTempPath(), "scope-tests", .. parts]);

    private sealed class FakeResolver : IPathResolver
    {
        private readonly Dictionary<string, PathResolution> noFollow = [];
        private readonly Dictionary<string, PathResolution> follow = [];

        public FakeResolver Directory(string path, string? finalPath = null) => Add(path, finalPath ?? path, FileAttributes.Directory);

        public FakeResolver File(string path, string? finalPath = null) => Add(path, finalPath ?? path, FileAttributes.Normal);

        public FakeResolver Link(string path, string? destination)
        {
            noFollow[path] = PathResolution.Resolved(path, FileAttributes.Directory | FileAttributes.ReparsePoint);
            follow[path] = destination is null
                ? PathResolution.Rejected(PathRejectionReason.NotFound, "dangling")
                : PathResolution.Resolved(destination, FileAttributes.Directory);
            return this;
        }

        public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow) =>
            (links == LinkHandling.Follow ? follow : noFollow).GetValueOrDefault(path)
            ?? PathResolution.Rejected(PathRejectionReason.NotFound, $"'{path}' does not exist.");

        private FakeResolver Add(string path, string finalPath, FileAttributes attributes)
        {
            noFollow[path] = follow[path] = PathResolution.Resolved(finalPath, attributes);
            return this;
        }
    }
}
