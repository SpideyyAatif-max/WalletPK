using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.UnitTests;

/// <summary>M12.2: matching a file against WalletPK's records, and every kind of break.</summary>
public sealed class MatcherTests
{
    private const string Party = "biller:KE";
    private static readonly DateOnly Day = new(2026, 11, 15);
    private static readonly DateTimeOffset Noon = new(2026, 11, 15, 7, 0, 0, TimeSpan.Zero);

    private static OurPayment Ours(long amount = 10_000, OurStatus status = OurStatus.Completed, string? reference = null, string party = Party) =>
        new(Guid.NewGuid(), party, reference ?? $"KE{Guid.NewGuid():N}"[..20].ToUpperInvariant(), amount, status, status == OurStatus.Completed ? Noon : null);

    private static SettlementLine Line(OurPayment payment, long? amount = null) =>
        new(payment.ProviderReference!, payment.Id.ToString(), amount ?? payment.AmountPaisa, Noon);

    private static SettlementFile File(params SettlementLine[] lines) => new(Party, Day, lines);

    private static MatchResult Match(SettlementFile file, IReadOnlyCollection<OurPayment> ours, IReadOnlySet<Guid>? elsewhere = null) =>
        Matcher.Match(file, ours, [.. ours.Where(payment => payment.Status == OurStatus.Completed)], elsewhere ?? new HashSet<Guid>());

    [Fact]
    public void A_file_that_agrees_has_no_breaks()
    {
        var ours = Enumerable.Range(1, 5).Select(index => Ours(index * 1_000)).ToList();

        var result = Match(File([.. ours.Select(payment => Line(payment))]), ours);

        result.Breaks.ShouldBeEmpty();
        result.Matched.ShouldBe(5);
        result.Lines.ShouldBe(5);
        result.OurCount.ShouldBe(5);
        result.ProviderTotalPaisa.ShouldBe(15_000);
        result.OurTotalPaisa.ShouldBe(15_000);
    }

    [Fact]
    public void A_payment_completed_here_but_not_in_the_file_is_missing_at_the_provider()
    {
        var listed = Ours();
        var missing = Ours(7_500);

        var result = Match(File(Line(listed)), [listed, missing]);

        var item = result.Breaks.ShouldHaveSingleItem();
        item.Kind.ShouldBe(BreakKind.MissingAtProvider);
        item.Key.ShouldBe(missing.Id.ToString());
        item.OurId.ShouldBe(missing.Id);
        item.OurAmountPaisa.ShouldBe(7_500);
        item.ProviderAmountPaisa.ShouldBeNull();
        item.LinesInFile.ShouldBe(0);
        result.Matched.ShouldBe(1);
    }

    [Fact]
    public void A_payment_settled_on_the_day_before_or_after_is_not_missing()
    {
        var listed = Ours();
        var acrossMidnight = Ours();

        var result = Match(File(Line(listed)), [listed, acrossMidnight], new HashSet<Guid> { acrossMidnight.Id });

        result.Breaks.ShouldBeEmpty();
    }

    [Fact]
    public void A_line_for_a_payment_never_sent_is_unknown()
    {
        var line = new SettlementLine("KE2611150000UNKNOWN", Guid.NewGuid().ToString(), 4_200, Noon);

        var item = Match(File(line), []).Breaks.ShouldHaveSingleItem();

        item.Kind.ShouldBe(BreakKind.UnknownToUs);
        item.Key.ShouldBe("KE2611150000UNKNOWN");
        item.OurId.ShouldBeNull();
        item.ProviderAmountPaisa.ShouldBe(4_200);
    }

    [Fact]
    public void A_line_for_another_partys_payment_is_unknown_here()
    {
        var elsewhere = Ours(party: "biller:LESCO");

        var item = Matcher.Match(File(Line(elsewhere)), [elsewhere], [], new HashSet<Guid>()).Breaks.ShouldHaveSingleItem();

        item.Kind.ShouldBe(BreakKind.UnknownToUs);
        item.Description.ShouldContain("biller:LESCO");
    }

    [Fact]
    public void A_line_listed_twice_is_one_duplicate()
    {
        var payment = Ours();
        var line = Line(payment);

        var result = Match(File(line, line, line), [payment]);

        var item = result.Breaks.ShouldHaveSingleItem();
        item.Kind.ShouldBe(BreakKind.Duplicate);
        item.LinesInFile.ShouldBe(3);
        item.OurId.ShouldBe(payment.Id);
        result.Matched.ShouldBe(0);
        result.ProviderTotalPaisa.ShouldBe(3 * payment.AmountPaisa);
    }

    [Fact]
    public void A_different_amount_is_an_amount_mismatch()
    {
        var payment = Ours(10_000);

        var item = Match(File(Line(payment, 10_250)), [payment]).Breaks.ShouldHaveSingleItem();

        item.Kind.ShouldBe(BreakKind.AmountMismatch);
        item.OurAmountPaisa.ShouldBe(10_000);
        item.ProviderAmountPaisa.ShouldBe(10_250);
        item.Description.ShouldContain("+Rs. 2.50");
    }

    [Theory]
    [InlineData(OurStatus.Failed, BreakKind.SettledButFailedHere)]
    [InlineData(OurStatus.Pending, BreakKind.SettledButPendingHere)]
    public void A_settled_line_for_a_payment_not_completed_here_is_flagged(OurStatus status, BreakKind kind)
    {
        var payment = Ours(status: status);

        var result = Match(File(Line(payment)), [payment]);

        var item = result.Breaks.ShouldHaveSingleItem();
        item.Kind.ShouldBe(kind);
        item.OurStatus.ShouldBe(status);
        result.OurCount.ShouldBe(0);
    }

    [Fact]
    public void A_line_without_our_id_is_found_by_the_providers_reference()
    {
        var payment = Ours();
        var line = new SettlementLine(payment.ProviderReference!, "not-a-guid", payment.AmountPaisa, Noon);

        Match(File(line), [payment]).Matched.ShouldBe(1);
    }

    /// <summary>
    /// Scenario S8 in small: plant every kind of mismatch at random, as the mock providers do, and the
    /// matcher must find exactly those, each classified correctly, and nothing else.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Every_planted_mismatch_is_found_and_classified(int seed)
    {
        var random = new Random(seed);
#pragma warning disable CA5394 // Seeded on purpose: repeatable.
        var ours = Enumerable.Range(0, 60).Select(_ => Ours(random.Next(100, 500_000))).ToList();
        var shuffled = ours.OrderBy(_ => random.Next()).ToList();
        var missing = shuffled.Take(random.Next(1, 6)).ToList();
        var duplicate = shuffled.Skip(missing.Count).Take(random.Next(1, 6)).ToList();
        var changed = shuffled.Skip(missing.Count + duplicate.Count).Take(random.Next(1, 6)).ToList();
        var unknown = Enumerable.Range(0, random.Next(1, 6))
            .Select(index => new SettlementLine($"UNK{index:D4}{random.Next():D10}", Guid.NewGuid().ToString(), random.Next(100, 5_000), Noon)).ToList();
#pragma warning restore CA5394

        var lines = new List<SettlementLine>();
        foreach (var payment in ours.Except(missing))
        {
            var line = changed.Contains(payment) ? Line(payment, payment.AmountPaisa + 1_234) : Line(payment);
            lines.Add(line);
            if (duplicate.Contains(payment))
            {
                lines.Add(line);
            }
        }

        var result = Match(File([.. lines.Concat(unknown).OrderBy(line => line.ProviderReference, StringComparer.Ordinal)]), ours);

        Keys(result, BreakKind.MissingAtProvider).ShouldBe(missing.Select(payment => payment.Id.ToString()).Order(StringComparer.Ordinal));
        Keys(result, BreakKind.Duplicate).ShouldBe(duplicate.Select(payment => payment.ProviderReference!).Order(StringComparer.Ordinal));
        Keys(result, BreakKind.AmountMismatch).ShouldBe(changed.Select(payment => payment.ProviderReference!).Order(StringComparer.Ordinal));
        Keys(result, BreakKind.UnknownToUs).ShouldBe(unknown.Select(line => line.ProviderReference).Order(StringComparer.Ordinal));
        result.Breaks.Count.ShouldBe(missing.Count + duplicate.Count + changed.Count + unknown.Count);
        result.Matched.ShouldBe(ours.Count - missing.Count - duplicate.Count - changed.Count);
    }

    [Fact]
    public void The_same_inputs_give_the_same_breaks_in_the_same_order()
    {
        var a = Ours();
        var b = Ours();
        var file = File(Line(a, 1), new SettlementLine("ZZZ", "x", 5, Noon), new SettlementLine("AAA", "y", 5, Noon));

        var first = Match(file, [a, b]);
        var second = Match(file, [b, a]);

        second.Breaks.ShouldBe(first.Breaks);
        first.Breaks.Select(item => item.Kind).ShouldBe([BreakKind.MissingAtProvider, BreakKind.UnknownToUs, BreakKind.UnknownToUs, BreakKind.AmountMismatch]);
    }

    [Theory]
    [InlineData(150_000, false, "Rs. 1,500.00")]
    [InlineData(-250, true, "-Rs. 2.50")]
    [InlineData(250, true, "+Rs. 2.50")]
    public void Amounts_read_as_rupees(long paisa, bool signed, string text) =>
        Matcher.Rupees(paisa, signed).ShouldBe(text);

    private static IEnumerable<string> Keys(MatchResult result, BreakKind kind) =>
        result.Breaks.Where(item => item.Kind == kind).Select(item => item.Key);
}
