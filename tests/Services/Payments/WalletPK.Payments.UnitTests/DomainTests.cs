using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Domain;

namespace WalletPK.Payments.UnitTests;

public class DomainTests
{
    [Theory]
    [InlineData("PK36SCBL0000001123456702", "PK36SCBL0000001123456702")]
    [InlineData("pk36 scbl 0000 0011 2345 6702", "PK36SCBL0000001123456702")]
    [InlineData("PK36-SCBL-0000-0011-2345-6702", "PK36SCBL0000001123456702")]
    [InlineData("PK37SCBL0000001123456702", null)]
    [InlineData("PK36SCBL000000112345670", null)]
    [InlineData("GB36SCBL0000001123456702", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Ibans_are_normalized_and_their_check_digits_verified(string? input, string? expected) =>
        Iban.Normalize(input).ShouldBe(expected);

    [Fact]
    public void An_iban_is_shown_with_its_account_number_hidden() =>
        Iban.Mask("PK36SCBL0000001123456702").ShouldBe("PK36 SCBL **** **** **** 6702");

    [Fact]
    public void Banks_are_found_by_their_iban_code_and_by_their_own_code()
    {
        RaastBanks.ByIbanCode("HABB")!.Code.ShouldBe("HBL");
        RaastBanks.ByCode("meezan")!.IbanCode.ShouldBe("MEZN");
        RaastBanks.ByIbanCode("SCBL").ShouldBeNull();
    }

    [Fact]
    public void The_same_idempotency_key_gives_the_same_transfer_id_per_user()
    {
        var user = Guid.NewGuid();
        var id = BankTransferService.TransferIdFor(user, "key-1");

        BankTransferService.TransferIdFor(user, "key-1").ShouldBe(id);
        BankTransferService.TransferIdFor(user, "key-2").ShouldNotBe(id);
        BankTransferService.TransferIdFor(Guid.NewGuid(), "key-1").ShouldNotBe(id);
        id.Version.ShouldBe(8);
    }

    [Fact]
    public void Back_off_doubles_up_to_its_cap_and_jitter_only_shortens_it()
    {
        BankTransferSaga.Backoff(15, 300, 1, jitter: false).ShouldBe(TimeSpan.FromSeconds(15));
        BankTransferSaga.Backoff(15, 300, 3, jitter: false).ShouldBe(TimeSpan.FromSeconds(60));
        BankTransferSaga.Backoff(15, 300, 50, jitter: false).ShouldBe(TimeSpan.FromSeconds(300));
        for (var i = 0; i < 100; i++)
        {
            BankTransferSaga.Backoff(15, 300, 2, jitter: true).TotalSeconds.ShouldBeInRange(24, 30);
        }
    }

    [Fact]
    public void Amounts_are_shown_in_rupees() =>
        BankTransferSaga.Rupees(125_050).ShouldBe("Rs. 1,250.5");

    [Fact]
    public void The_reserved_money_must_outlive_the_queue_and_the_wait()
    {
        new BankTransferOptions().IsValid().ShouldBeTrue();
        new BankTransferOptions { HoldLifetimeHours = 72, MaxQueueHours = 24, MaxWaitHours = 48 }.IsValid().ShouldBeFalse();
        new BankTransferOptions { HoldLifetimeHours = 169 }.IsValid().ShouldBeFalse();
    }
}
