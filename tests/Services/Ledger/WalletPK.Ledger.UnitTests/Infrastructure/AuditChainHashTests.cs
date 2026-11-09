using FsCheck;
using FsCheck.Fluent;
using WalletPK.Ledger.Infrastructure.Audit;

namespace WalletPK.Ledger.UnitTests.Infrastructure;

public class AuditChainHashTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static AuditRecord Record(
        string eventType = "journal.posted", string subjectType = "journal", string subjectId = "0193f1c4", string actor = "system", string? correlation = null, string payload = "{}") =>
        AuditRecord.Create(_at, eventType, subjectType, subjectId, actor, correlation, payload);

    [Fact]
    public void The_hash_is_lowercase_hex_sha256_and_deterministic()
    {
        var hash = AuditChain.ComputeHash(Record(), 1, AuditChain.GenesisHash);

        hash.Length.ShouldBe(64);
        hash.ShouldAllBe(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));
        AuditChain.ComputeHash(Record(), 1, AuditChain.GenesisHash).ShouldBe(hash);
    }

    [Fact]
    public void Every_field_and_the_link_are_covered_by_the_hash()
    {
        var original = AuditChain.ComputeHash(Record(), 7, AuditChain.GenesisHash);

        new[]
        {
            AuditChain.ComputeHash(Record(eventType: "journal.reversed"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(subjectType: "hold"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(subjectId: "0193f1c5"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(actor: "user:1"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(correlation: "abc"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(payload: "{ }"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(AuditRecord.Create(_at.AddTicks(10), "journal.posted", "journal", "0193f1c4", "system", null, "{}"), 7, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(), 8, AuditChain.GenesisHash),
            AuditChain.ComputeHash(Record(), 7, new string('1', 64)),
        }.ShouldAllBe(hash => hash != original);
    }

    [Fact]
    public void Moving_text_between_fields_changes_the_hash()
    {
        // Without length prefixes "ab"+"c" and "a"+"bc" would hash the same.
        AuditChain.ComputeHash(Record(eventType: "ab", subjectType: "c"), 1, AuditChain.GenesisHash)
            .ShouldNotBe(AuditChain.ComputeHash(Record(eventType: "a", subjectType: "bc"), 1, AuditChain.GenesisHash));
        AuditChain.ComputeHash(Record(correlation: null), 1, AuditChain.GenesisHash)
            .ShouldBe(AuditChain.ComputeHash(Record(correlation: string.Empty), 1, AuditChain.GenesisHash));
    }

    [Fact]
    public void Any_change_to_a_payload_changes_the_hash()
    {
        var property = Prop.ForAll(
            Arb.From(ArbMap.Default.GeneratorFor<NonEmptyString>()),
            Arb.From(Gen.Choose(0, 1_000)),
            (payload, position) =>
            {
                var text = payload.Get;
                var index = position % text.Length;
                var tampered = string.Concat(text.AsSpan(0, index), text[index] == 'x' ? "y" : "x", text.AsSpan(index + 1));

                AuditChain.ComputeHash(Record(payload: tampered), 1, AuditChain.GenesisHash)
                    .ShouldNotBe(AuditChain.ComputeHash(Record(payload: text), 1, AuditChain.GenesisHash));
            });

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(500), property);
    }

    [Fact]
    public void Times_are_kept_to_the_microsecond_like_postgresql()
    {
        var record = AuditRecord.Create(_at.AddTicks(1_234_567), "a", "b", "c", "d", null, "{}");

        (record.OccurredAt.UtcTicks % 10).ShouldBe(0);
        record.OccurredAt.ShouldBe(_at.AddTicks(1_234_560));
    }
}
