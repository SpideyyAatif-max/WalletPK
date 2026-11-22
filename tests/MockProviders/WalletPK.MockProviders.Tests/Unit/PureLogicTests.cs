using WalletPK.MockProviders.Banks;
using WalletPK.MockProviders.Billers;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Nadra;
using WalletPK.MockProviders.Settlement;
using WalletPK.MockProviders.Telcos;

namespace WalletPK.MockProviders.Tests.Unit;

public sealed class IbanTests
{
    [Theory]
    [InlineData("PK36SCBL0000001123456702")] // The State Bank's published example.
    [InlineData("pk36 scbl 0000 0011 2345 6702")]
    public void Valid_ibans_are_accepted_in_any_spacing_and_case(string input) =>
        Iban.Normalize(input).ShouldBe("PK36SCBL0000001123456702");

    [Theory]
    [InlineData("PK37SCBL0000001123456702")] // wrong check digits
    [InlineData("PK36SCBL0000001123456703")] // typo in the account number
    [InlineData("GB36SCBL0000001123456702")]
    [InlineData("PK36SCBL000000112345670")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_ibans_are_refused(string? input) => Iban.Normalize(input).ShouldBeNull();

    [Fact]
    public void Generated_ibans_validate_and_one_changed_digit_breaks_them()
    {
        for (var i = 0; i < 200; i++)
        {
            var number = Random.Shared.NextInt64(0, 10_000_000_000_000_000).ToString("D16", System.Globalization.CultureInfo.InvariantCulture);
            var iban = Iban.For("HABB", number);
            Iban.Normalize(iban).ShouldBe(iban);

            var position = 8 + Random.Shared.Next(16);
            var digit = (char)('0' + (((iban[position] - '0') + 1 + Random.Shared.Next(9)) % 10));
            Iban.Normalize(iban[..position] + digit + iban[(position + 1)..]).ShouldBeNull();
        }
    }

    [Fact]
    public void Aliases_always_point_to_the_same_valid_account()
    {
        var account = BankDirectory.ByAlias("+923001234567")!;

        BankDirectory.ByAlias("+923001234567").ShouldBe(account);
        Iban.Normalize(account.Iban).ShouldBe(account.Iban);
        account.Status.ShouldBe(AccountStatus.Active);
        BankDirectory.ByAlias("+923001234000").ShouldBeNull();
    }
}

public sealed class NameMatcherTests
{
    [Theory]
    [InlineData("Muhammad Ali Khan", "MUHAMMAD ALI KHAN")]
    [InlineData("Muhammad Ali Khan", "Mohammad  Ali-Khan")]
    [InlineData("Muhammad Ali Khan", "Khan, Muhammad Ali")]
    [InlineData("Muhammad Ali Khan", "M. Ali Khan")]
    [InlineData("Ayesha Siddiqui", "Ayesha Sidiqui")]
    public void The_same_person_matches(string a, string b) => NameMatcher.Matches(a, b).ShouldBeTrue($"{NameMatcher.Score(a, b)}");

    [Theory]
    [InlineData("Muhammad Ali Khan", "Ayesha Siddiqui")]
    [InlineData("Ali Raza", "Ali Hassan")]
    [InlineData("Fatima Butt", "")]
    public void Different_people_do_not(string a, string b) => NameMatcher.Matches(a, b).ShouldBeFalse($"{NameMatcher.Score(a, b)}");
}

public sealed class SettlementFileTests
{
    private static readonly DateOnly _day = new(2026, 11, 15);

    private static List<SettlementLine> Lines(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new SettlementLine($"REF{i:D4}", $"client-{i}", i * 1_000, PakistanTime.StartOf(_day).AddMinutes(i)))];

    [Fact]
    public void Without_a_plan_the_file_is_the_truth()
    {
        var lines = Lines(5);

        var (file, injected) = SettlementFile.Apply(lines, new MismatchPlan(), "bank:HBL", _day);

        file.ShouldBe(lines);
        injected.Missing.ShouldBeEmpty();
        SettlementFile.ToCsv("bank:HBL", _day, file).ShouldEndWith("T,5,15000\n");
    }

    [Fact]
    public void Each_kind_of_mismatch_touches_different_items_and_the_same_plan_gives_the_same_file()
    {
        var lines = Lines(20);
        var plan = new MismatchPlan(Missing: 3, Duplicate: 2, AmountMismatch: 4, Unknown: 2);

        var (file, injected) = SettlementFile.Apply(lines, plan, "biller:KE", _day);
        var (again, _) = SettlementFile.Apply(lines, plan, "biller:KE", _day);

        again.ShouldBe(file);
        var touched = injected.Missing.Concat(injected.Duplicate).Concat(injected.AmountMismatch.Select(change => change.ProviderReference)).ToList();
        touched.Distinct().Count().ShouldBe(9);
        file.Count.ShouldBe(20 - 3 + 2 + 2);
        injected.AmountMismatch.ShouldAllBe(change => change.ReportedPaisa != change.ActualPaisa && change.ReportedPaisa > 0);
        injected.Unknown.ShouldAllBe(line => !lines.Any(real => real.ProviderReference == line.ProviderReference));
    }

    [Fact]
    public void A_plan_bigger_than_the_day_plants_what_it_can()
    {
        var (file, injected) = SettlementFile.Apply(Lines(2), new MismatchPlan(Missing: 5, Duplicate: 5), "telco:ZONG", _day);

        injected.Missing.Count.ShouldBe(2);
        injected.Duplicate.ShouldBeEmpty();
        file.ShouldBeEmpty();
    }
}

public sealed class FaultTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Faults_with_a_duration_disappear_when_it_is_over()
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        var registry = new FaultRegistry(clock);
        registry.Set("nadra", new FaultSettings(Outage: true, DurationSeconds: 60));
        registry.Set("raast", new FaultSettings(LatencyMs: 10));

        clock.Now = clock.Now.AddSeconds(59);
        registry.For("nadra").ShouldNotBeNull();

        clock.Now = clock.Now.AddSeconds(1);
        registry.For("nadra").ShouldBeNull();
        registry.All().Select(active => active.Provider).ShouldBe(["raast"]);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "latencyMs")]
    [InlineData(0, 1.5, 0, 0, "Rates")]
    [InlineData(0, 0.5, 0.4, 0.2, "must not exceed 1")]
    public void Nonsense_settings_are_refused(int latency, double error, double timeout, double acceptButTimeout, string expected) =>
        new FaultSettings(LatencyMs: latency, ErrorRate: error, TimeoutRate: timeout, AcceptButTimeoutRate: acceptButTimeout).Problem()!.ShouldContain(expected);

    [Fact]
    public void Provider_keys_cover_every_party()
    {
        Providers.All.Count.ShouldBe(16);
        Providers.IsKnown("bank:MEEZAN").ShouldBeTrue();
        Providers.IsKnown("bank:meezan").ShouldBeFalse();
    }
}

public sealed class CatalogTests
{
    [Fact]
    public void Bills_are_fixed_for_the_month_and_cost_10_percent_more_after_the_20th()
    {
        var ke = BillerCatalog.ByCode("ke")!;

        var early = BillerCatalog.BillFor(ke, "0400123456789", new DateOnly(2026, 11, 5), paid: false);
        var due = BillerCatalog.BillFor(ke, "0400123456789", new DateOnly(2026, 11, 20), paid: false);
        var late = BillerCatalog.BillFor(ke, "0400123456789", new DateOnly(2026, 11, 21), paid: false);
        var next = BillerCatalog.BillFor(ke, "0400123456789", new DateOnly(2026, 12, 5), paid: false);

        due.AmountWithinDuePaisa.ShouldBe(early.AmountWithinDuePaisa);
        early.PayablePaisa.ShouldBe(early.AmountWithinDuePaisa);
        due.PayablePaisa.ShouldBe(due.AmountWithinDuePaisa);
        late.PayablePaisa.ShouldBe(late.AmountAfterDuePaisa);
        late.AmountAfterDuePaisa.ShouldBe((long)Math.Round(late.AmountWithinDuePaisa / 100 * 1.1m, MidpointRounding.AwayFromZero) * 100);
        early.AmountWithinDuePaisa.ShouldBeInRange(ke.MinBillRupees * 100, ke.MaxBillRupees * 100);
        next.BillingMonth.ShouldBe("2026-12");
    }

    [Theory]
    [InlineData("+923001234567", "JAZZ")]
    [InlineData("+923211234567", "JAZZ")]
    [InlineData("+923151234567", "ZONG")]
    [InlineData("+923451234567", "TELENOR")]
    [InlineData("+923331234567", "UFONE")]
    public void Numbers_belong_to_networks_by_prefix(string number, string telco) =>
        TelcoCatalog.NetworkOf(number)!.Code.ShouldBe(telco);

    [Theory]
    [InlineData("0300-1234567", "+923001234567")]
    [InlineData("+92 300 1234567", "+923001234567")]
    [InlineData("923001234567", "+923001234567")]
    [InlineData("042-1234567", null)]
    public void Mobile_numbers_are_read_in_any_common_form(string input, string? expected) =>
        Mobile.ToE164(input).ShouldBe(expected);

    [Fact]
    public void Business_dates_are_pakistani_dates() =>
        PakistanTime.BusinessDate(new DateTimeOffset(2026, 11, 14, 19, 30, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 11, 15));
}
