using WalletPK.Reconciliation.Application;
using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.UnitTests;

/// <summary>M12.1: when the nightly run reconciles yesterday, and which parties it does.</summary>
public sealed class ScheduleTests
{
    private static readonly TimeOnly HalfPastMidnight = new(0, 30);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(15);
    private static readonly string[] Parties = ["bank:HBL", "biller:KE", "telco:JAZZ"];

    /// <summary>Pakistan time on 16 November 2026.</summary>
    private static DateTimeOffset At(int hour, int minute) => new(2026, 11, 16, hour, minute, 0, TimeSpan.FromHours(5));

    private static readonly DateOnly Yesterday = new(2026, 11, 15);

    private static ReconRun Run(string party, string status, DateTimeOffset finishedAt) =>
        new() { Party = party, BusinessDate = Yesterday, Status = status, StartedAt = finishedAt, FinishedAt = finishedAt };

    [Fact]
    public void Nothing_is_due_before_the_time()
    {
        var (date, parties) = ReconSchedule.Due(At(0, 29), HalfPastMidnight, Parties, [], Retry);

        date.ShouldBe(Yesterday);
        parties.ShouldBeEmpty();
    }

    [Fact]
    public void After_the_time_every_party_is_due_for_yesterday()
    {
        var (date, parties) = ReconSchedule.Due(At(0, 30), HalfPastMidnight, Parties, [], Retry);

        date.ShouldBe(Yesterday);
        parties.ShouldBe(Parties);
    }

    [Fact]
    public void The_business_day_is_Pakistans_not_UTCs()
    {
        // 20:00 UTC on the 15th is 01:00 on the 16th in Pakistan: the 15th is done.
        var (date, parties) = ReconSchedule.Due(new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero), HalfPastMidnight, Parties, [], Retry);

        date.ShouldBe(Yesterday);
        parties.Count.ShouldBe(3);
    }

    [Fact]
    public void A_party_already_reconciled_is_not_run_again()
    {
        var runs = new[] { Run("bank:HBL", RunStatuses.Completed, At(0, 31)) };

        ReconSchedule.Due(At(9, 0), HalfPastMidnight, Parties, runs, Retry).Parties.ShouldBe(["biller:KE", "telco:JAZZ"]);
    }

    [Fact]
    public void A_party_whose_file_was_not_there_is_tried_again_later()
    {
        var runs = new[] { Run("bank:HBL", RunStatuses.Failed, At(0, 31)) };

        ReconSchedule.Due(At(0, 40), HalfPastMidnight, Parties, runs, Retry).Parties.ShouldNotContain("bank:HBL");
        ReconSchedule.Due(At(0, 46), HalfPastMidnight, Parties, runs, Retry).Parties.ShouldContain("bank:HBL");
    }

    [Fact]
    public void The_latest_run_of_a_party_decides()
    {
        var runs = new[] { Run("bank:HBL", RunStatuses.Failed, At(0, 31)), Run("bank:HBL", RunStatuses.Completed, At(0, 50)) };

        ReconSchedule.Due(At(9, 0), HalfPastMidnight, Parties, runs, Retry).Parties.ShouldNotContain("bank:HBL");
    }

    [Theory]
    [InlineData("MissingAtProvider", 5_000L, null, 0, 5_000L)]
    [InlineData("UnknownToUs", null, 4_000L, 1, 4_000L)]
    [InlineData("Duplicate", 3_000L, 3_000L, 3, 6_000L)]
    [InlineData("AmountMismatch", 3_000L, 3_250L, 1, 250L)]
    [InlineData("SettledButFailedHere", 3_000L, 3_000L, 1, 3_000L)]
    public void A_break_says_how_much_money_it_is_about(string kind, long? ours, long? theirs, int lines, long atStake) =>
        new ReconBreak { Kind = kind, OurAmountPaisa = ours, ProviderAmountPaisa = theirs, LinesInFile = lines }.AtStakePaisa.ShouldBe(atStake);

    [Theory]
    [InlineData("Completed", OurStatus.Completed)]
    [InlineData("Failed", OurStatus.Failed)]
    [InlineData("Processing", OurStatus.Pending)]
    [InlineData("UnderReview", OurStatus.Pending)]
    [InlineData("Queued", OurStatus.Pending)]
    public void Each_services_status_reads_as_ours(string status, OurStatus ours) =>
        PaymentTracker.StatusOf(status).ShouldBe(ours);
}
