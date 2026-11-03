using System.Security.Cryptography;
using System.Text;
using jd.resolver.release;
using Xunit;

namespace jd.resolver.tests;

// The write-once store of release records: nothing is opened for write twice, names cannot leave the store, set.yaml is never overwritten.
public sealed class ReleaseRecordFileTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 8, 30, 15, 123, TimeSpan.Zero);

    private readonly MemoryRecordStore _records = new();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_record_is_one_new_file_named_by_its_utc_time_and_activity()
    {
        var path = await ReleaseRecordFile.WriteAsync(_records, "dev", "1.0.0", At.ToOffset(TimeSpan.FromHours(2)), "deploy", "{}\n");

        Assert.Equal("dev/1.0.0/20261017T083015123Z-deploy.json", path);
        Assert.Equal("{}\n", _records.Text(path));
    }

    [Fact]
    public async Task A_record_path_that_exists_is_never_opened_again_and_a_later_record_leaves_the_old_bytes_unchanged()
    {
        var first = await ReleaseRecordFile.WriteAsync(_records, "dev", "1.0.0", At, "deploy", "first");

        await Assert.ThrowsAnyAsync<IOException>(() => ReleaseRecordFile.WriteAsync(_records, "dev", "1.0.0", At, "deploy", "second"));
        var next = await ReleaseRecordFile.WriteAsync(_records, "dev", "1.0.0", At.AddMilliseconds(1), "deploy", "third");

        Assert.Equal("first", _records.Text(first));
        Assert.Equal("third", _records.Text(next));
    }

    [Theory]
    [InlineData("..", "1.0.0")]
    [InlineData("dev", "..")]
    [InlineData("../dev", "1.0.0")]
    [InlineData("dev", "1.0.0/../../x")]
    [InlineData("dev", "a/b")]
    [InlineData("a\\b", "1.0.0")]
    [InlineData("dev", "1..0")]
    [InlineData("", "1.0.0")]
    [InlineData("dev", "")]
    public async Task An_environment_or_label_with_dots_or_a_separator_is_rejected_by_both_operations_and_nothing_is_created(string environment, string label)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseRecordFile.WriteAsync(_records, environment, label, At, "deploy", "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseRecordFile.KeepSetAsync(_records, environment, label, Bytes("set")));

        Assert.Empty(_records.Files);
    }

    // The fake refuses what LocalRecordStore refuses, by the same guard, so a test cannot pass on a name the real store would refuse.
    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task The_store_fake_refuses_a_name_that_is_not_one_segment_by_the_ports_guard(string segment)
    {
        var refusals = new[]
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _records.CreateAsync("dev", "1.0.0", segment, Bytes("x"), CancellationToken.None)),
            await Assert.ThrowsAsync<ArgumentException>(() => _records.ReadAsync(segment, "1.0.0", "set.yaml", CancellationToken.None)),
            await Assert.ThrowsAsync<ArgumentException>(() => _records.ListAsync("dev", segment, CancellationToken.None)),
        };

        Assert.All(refusals, refusal => Assert.Equal($"'{segment}' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.", refusal.Message));
        Assert.Empty(_records.Files);
    }

    [Fact]
    public async Task The_first_attempt_keeps_the_sets_exact_bytes_and_returns_their_hash()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', 0xFF }; // a byte order mark, CRLF and invalid UTF-8 all survive

        var (sha256, problem) = await ReleaseRecordFile.KeepSetAsync(_records, "dev", "1.0.0", bytes);

        Assert.Null(problem);
        Assert.Equal(bytes, _records.Files["dev/1.0.0/set.yaml"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256);
    }

    [Fact]
    public async Task The_same_set_again_reuses_set_yaml_and_a_different_one_is_refused_without_touching_it()
    {
        await ReleaseRecordFile.KeepSetAsync(_records, "dev", "1.0.0", Bytes("one"));

        var (_, again) = await ReleaseRecordFile.KeepSetAsync(_records, "dev", "1.0.0", Bytes("one"));
        var (sha256, different) = await ReleaseRecordFile.KeepSetAsync(_records, "dev", "1.0.0", Bytes("two"));
        var (_, otherLabel) = await ReleaseRecordFile.KeepSetAsync(_records, "dev", "1.0.1", Bytes("two"));

        Assert.Null(again);
        Assert.Contains("the kept set.yaml holds a different release set for label 1.0.0 on dev", different, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes("two"))), sha256);
        Assert.Equal("one", _records.Text("dev/1.0.0/set.yaml"));
        Assert.Null(otherLabel);
    }
}
