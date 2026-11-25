using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.UnitTests;

/// <summary>M12.1: reading settlement files, and refusing broken ones whole.</summary>
public sealed class SettlementCsvTests
{
    private static readonly DateOnly Day = new(2026, 11, 15);

    private const string Good =
        "H,bank:HBL,2026-11-15\n" +
        "D,RAAST20261115AAAA,7d0e8c2a-55b1-4d0e-9a59-0d5e2c0a1b11,150000,2026-11-15T04:12:44.0000000+00:00\n" +
        "D,RAAST20261115BBBB,0f6f2e64-3a3b-4c59-8f3e-6a0b1d9f7c22,250050,2026-11-15T09:30:00.0000000+00:00\n" +
        "T,2,400050\n";

    [Fact]
    public void A_good_file_is_read_line_by_line()
    {
        var (file, problem) = SettlementCsv.Parse(Good, "bank:HBL", Day);

        problem.ShouldBeNull();
        file.ShouldNotBeNull();
        file.Party.ShouldBe("bank:HBL");
        file.BusinessDate.ShouldBe(Day);
        file.Lines.Count.ShouldBe(2);
        file.TotalPaisa.ShouldBe(400_050);
        file.Lines[1].ShouldBe(new SettlementLine("RAAST20261115BBBB", "0f6f2e64-3a3b-4c59-8f3e-6a0b1d9f7c22", 250_050, new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Windows_line_endings_and_an_empty_day_are_fine()
    {
        var (file, problem) = SettlementCsv.Parse("H,telco:JAZZ,2026-11-15\r\nT,0,0\r\n", "telco:JAZZ", Day);

        problem.ShouldBeNull();
        file!.Lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("H,bank:MCB,2026-11-15\nT,0,0\n", "names bank:MCB")]
    [InlineData("H,bank:HBL,2026-11-14\nT,0,0\n", "date is 2026-11-14")]
    [InlineData("X,bank:HBL,2026-11-15\nT,0,0\n", "not a header")]
    [InlineData("H,bank:HBL,2026-11-15\n", "no header and trailer")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,c1,100,2026-11-15T00:00:00Z\n", "not a trailer")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,c1,100,2026-11-15T00:00:00Z\nT,2,100\n", "counts 2 lines")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,c1,100,2026-11-15T00:00:00Z\nT,1,101\n", "totals 101")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,c1,-100,2026-11-15T00:00:00Z\nT,1,0\n", "Record 2")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,c1,100,yesterday\nT,1,100\n", "Record 2")]
    [InlineData("H,bank:HBL,2026-11-15\nD,R1,100,2026-11-15T00:00:00Z\nT,1,100\n", "Record 2")]
    public void A_broken_file_is_rejected_with_the_reason(string csv, string reason)
    {
        var (file, problem) = SettlementCsv.Parse(csv, "bank:HBL", Day);

        file.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(reason);
    }

    [Theory]
    [InlineData("bank:HBL", "banks/HBL")]
    [InlineData("biller:KE", "billers/KE")]
    [InlineData("telco:JAZZ", "telcos/JAZZ")]
    [InlineData("raast", null)]
    public void Each_party_has_a_file_location(string party, string? path)
    {
        Parties.FilePath(party).ShouldBe(path);
        Parties.IsValid(party).ShouldBe(path is not null);
    }

    [Fact]
    public void Every_default_party_is_valid() =>
        Parties.Default.ShouldAllBe(party => Parties.IsValid(party));
}
