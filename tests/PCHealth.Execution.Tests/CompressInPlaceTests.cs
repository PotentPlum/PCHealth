using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.Execution.Tests;

/// <summary>Compression through the gateway (T-55): checks, sizes on disk in the ledger, skips with reasons, and Undo.</summary>
public partial class ExecutionGatewayTests
{
    [Fact]
    public void Compression_records_the_size_on_disk_before_and_after_and_undo_decompresses()
    {
        using var fixture = new Fixture();
        var compression = new FakeCompression();
        var a = fixture.File("Logs/a.log", new string('a', 8000));
        var b = fixture.File("Logs/b.log", new string('b', 4000));
        var recommendation = Compress(fixture, a, b);

        var result = Gateway(fixture, compression).Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var record = fixture.Ledger.Find(result.TransactionId!.Value)!;
        Assert.Equal(LedgerOperation.CompressInPlace, record.Operation);
        Assert.Equal(RecoveryMethod.Decompress, record.RecoveryMethod);
        Assert.Equal([8000L, 4000L], record.Items.Select(i => i.Before.SizeOnDiskBytes!.Value));
        Assert.Equal([2000L, 1000L], record.Items.Select(i => i.After!.SizeOnDiskBytes!.Value));
        Assert.Equal(9000, record.ObservedBytes);
        Assert.Equal(6000, record.ExpectedBytes);
        Assert.Contains("compression supported: NTFS, not encrypted, not already compressed", record.Checks);
        Assert.Equal(new string('a', 8000), File.ReadAllText(a));

        var restorer = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock, compression: compression);
        Assert.StartsWith("2 files can be decompressed", restorer.SupportFor(record).Limits);
        var undo = restorer.Restore(record.Id).Restore!;

        Assert.Equal(TransactionState.Verified, undo.State);
        Assert.Equal("2 files are decompressed.", undo.Outcome);
        Assert.Empty(compression.Compressed);
        Assert.False(restorer.SupportFor(record).CanRestore);
    }

    [Fact]
    public void Files_in_use_unsupported_or_not_worth_compressing_are_skipped_with_a_reason()
    {
        using var fixture = new Fixture();
        var compression = new FakeCompression();
        var ok = fixture.File("Logs/ok.log", new string('o', 8000));
        var busy = fixture.File("Logs/busy.log", new string('b', 8000));
        var encrypted = fixture.File("Logs/secret.log", new string('s', 8000));
        var noise = fixture.File("Logs/noise.bin", new string('n', 8000));
        fixture.Platform.InUse[busy] = ["Editor"];
        compression.Unsupported[encrypted] = "It is encrypted, and Windows can't compress encrypted files.";
        compression.NotBeneficial.Add(noise);
        var recommendation = Compress(fixture, ok, busy, encrypted, noise);

        var result = Gateway(fixture, compression).Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.PartiallyCompleted, result.Status);
        Assert.Equal(
            [null, "It is in use by Editor.", "It is encrypted, and Windows can't compress encrypted files.", "Compressing it would not save space, so it was left as it is."],
            result.Items.Select(i => i.Reason));
        Assert.Equal([ItemStatus.Done, ItemStatus.Skipped, ItemStatus.Skipped, ItemStatus.Skipped], result.Items.Select(i => i.Status));
        Assert.Equal([ok], compression.Compressed);
    }

    [Fact]
    public void Undo_is_refused_up_front_when_the_drive_lacks_room_for_the_full_size()
    {
        using var fixture = new Fixture();
        var compression = new FakeCompression();
        var file = fixture.File("Logs/a.log", new string('a', 8000));
        var recommendation = Compress(fixture, file);
        var moved = Gateway(fixture, compression).Execute(recommendation, recommendation.Operations);
        compression.Free = 5999;

        var undo = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock, compression: compression).Restore(moved.TransactionId!.Value);

        Assert.Null(undo.Restore);
        Assert.False(undo.Support.CanRestore);
        Assert.Contains("needs 6,000 bytes free", undo.Support.Limits);
        Assert.Contains("only 5,999 bytes are free. Nothing was changed", undo.Support.Limits);
        Assert.Equal([file], compression.Compressed);
        Assert.Single(fixture.Ledger.All());
    }

    [Fact]
    public void A_file_written_since_is_no_longer_compressed_and_is_left_out_of_undo()
    {
        using var fixture = new Fixture();
        var compression = new FakeCompression();
        var a = fixture.File("Logs/a.log", new string('a', 8000));
        var b = fixture.File("Logs/b.log", new string('b', 8000));
        var recommendation = Compress(fixture, a, b);
        var done = Gateway(fixture, compression).Execute(recommendation, recommendation.Operations);
        compression.Compressed.Remove(b);

        var restorer = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock, compression: compression);
        Assert.StartsWith("1 files can be decompressed", restorer.SupportFor(fixture.Ledger.Find(done.TransactionId!.Value)!).Limits);
        var undo = restorer.Restore(done.TransactionId!.Value).Restore!;

        Assert.Equal([a], undo.Items.Select(i => i.OriginalPath));
    }

    [Fact]
    public void Without_a_compression_layer_nothing_is_compressed()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Logs/a.log", new string('a', 8000));
        var recommendation = Compress(fixture, file);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Refused, result.Status);
        Assert.Equal("Compression is not available here; nothing was changed.", result.Summary);
        Assert.Empty(fixture.Ledger.All());
    }

    [Fact]
    public void An_interrupted_compression_is_settled_from_what_the_file_system_reports()
    {
        using var fixture = new Fixture();
        var compression = new FakeCompression { CrashOnCompress = 2 };
        var a = fixture.File("Logs/a.log", new string('a', 8000));
        var b = fixture.File("Logs/b.log", new string('b', 8000));
        var recommendation = Compress(fixture, a, b);

        Assert.Throws<SimulatedCrash>(() => Gateway(fixture, compression).Execute(recommendation, recommendation.Operations));
        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store, compression).Resolve().Resolved);

        Assert.Equal([ItemStatus.Done, ItemStatus.Skipped], resolved.Items.Select(i => i.Status));
        Assert.Equal("Interrupted before it was compressed; it was left as it was.", resolved.Items[1].Error);
    }

    [Fact]
    public void Compression_choices_are_checked_by_policy()
    {
        using var fixture = new Fixture();
        var documents = fixture.Root.CreateDirectory("Documents");
        var text = fixture.File("Documents/notes.txt", new string('n', 8000));
        var program = fixture.File("Documents/tool.exe", new string('m', 8000));
        var protection = new ProtectionPolicy([new(documents, ProtectionKind.PersonalFolder, "it is your Documents folder.")]);
        var builder = new RecommendationBuilder(protection, new ExclusionList());

        var choice = builder.ForCompression(Estimate(documents, 6000), [Entry(text), Entry(program)]);

        Assert.Equal([text], choice.Recommendation!.Operations.Select(o => o.Path));
        Assert.Contains("program code", choice.Recommendation.Blocked.Single().Reason);
        Assert.Equal(FindingCategory.Compression, choice.Recommendation.Category);
        Assert.Equal(6000, choice.Recommendation.Impact.PotentiallyReclaimable!.Value.Bytes);
        Assert.StartsWith("Yes: Undo decompresses the files.", choice.Recommendation.CanIRecover);
        Assert.Contains("When a compressed file is changed, Windows decompresses it", choice.Recommendation.WhatCouldHappenAfterward);

        Assert.Equal("The estimate found nothing to save, so compression is not offered.", builder.ForCompression(Estimate(documents, 0), [Entry(text)]).Refusal);
        Assert.Equal("No file here can be compressed.", builder.ForCompression(Estimate(documents, 6000), []).Refusal);
        var exclusions = new ExclusionList();
        exclusions.ExcludeCategory(FindingCategory.Compression, Now);
        Assert.Equal("You excluded compression in Settings, so nothing is compressed.",
            new RecommendationBuilder(protection, exclusions).ForCompression(Estimate(documents, 6000), [Entry(text)]).Refusal);
    }

    private static ExecutionGateway Gateway(Fixture fixture, FakeCompression compression) =>
        new(fixture.Resolver, fixture.Platform, new ProtectionPolicy([]), fixture.Exclusions, fixture.Ledger, fixture.Store, fixture.Pause, fixture.Clock, compression: compression);

    private static Recommendation Compress(Fixture fixture, params string[] files)
    {
        var choice = new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList())
            .ForCompression(Estimate(Path.GetDirectoryName(files[0])!, 6000), files.Select(Entry).ToArray());
        Assert.True(choice.IsAllowed, choice.Refusal);
        return choice.Recommendation!;
    }

    private static CompressionEstimate Estimate(string folder, long saving) =>
        new(folder, CompressionAlgorithm.Xpress8K, new EstimatedSize(saving, "a test sample"), new MeasuredSize(16000), 2, 16000, 2, []);

    private static FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry(path, new MeasuredSize(info.Length), info.Attributes, info.LastWriteTimeUtc);
    }

    /// <summary>Compression as bookkeeping: a compressed file takes a quarter of its size on disk.</summary>
    private sealed class FakeCompression : IFileCompression
    {
        private int compressions;

        public List<string> Compressed { get; } = [];

        public Dictionary<string, string> Unsupported { get; } = [];

        public HashSet<string> NotBeneficial { get; } = [];

        public long Free { get; set; } = long.MaxValue;

        public int? CrashOnCompress { get; set; }

        public string? CannotCompress(string path) => Unsupported.GetValueOrDefault(path);

        public bool IsCompressed(string path) => Compressed.Contains(path);

        public long SizeOnDisk(string path) => new FileInfo(path).Length / (IsCompressed(path) ? 4 : 1);

        public long FreeSpace(string path) => Free;

        public void Compress(string path)
        {
            if (++compressions == CrashOnCompress)
            {
                throw new SimulatedCrash();
            }

            if (NotBeneficial.Contains(path))
            {
                throw new CompressionNotBeneficialException("Not beneficial.");
            }

            Compressed.Add(path);
        }

        public void Decompress(string path) => Compressed.Remove(path);
    }
}
