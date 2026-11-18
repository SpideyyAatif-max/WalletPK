using System.Text;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.UnitTests;

public class NetworkTests
{
    [Theory]
    [InlineData("+923001234567", "JAZZ")]
    [InlineData("+923251234567", "JAZZ")]
    [InlineData("+923151234567", "ZONG")]
    [InlineData("+923331234567", "UFONE")]
    [InlineData("+923451234567", "TELENOR")]
    public void A_number_belongs_to_the_network_of_its_prefix(string e164, string network) =>
        Networks.Of(e164)!.Code.ShouldBe(network);

    [Theory]
    [InlineData("+923551234567")]
    [InlineData("+92300123456")]
    [InlineData("+443001234567")]
    public void Other_numbers_belong_to_no_network(string e164) => Networks.Of(e164).ShouldBeNull();

    [Fact]
    public void Every_prefix_belongs_to_exactly_one_network() =>
        Networks.All.SelectMany(network => network.Prefixes).ShouldBeUnique();

    [Theory]
    [InlineData(2_000, true)]
    [InlineData(500_000, true)]
    [InlineData(12_300, true)]
    [InlineData(1_900, false)]
    [InlineData(500_100, false)]
    [InlineData(2_050, false)]
    public void Airtime_is_whole_rupees_from_20_to_5000(long paisa, bool valid) => TopUpRules.IsValidAirtime(paisa).ShouldBe(valid);

    [Fact]
    public void Retries_back_off_exponentially_up_to_a_ceiling()
    {
        TopUpSaga.Backoff(5, 60, 1).ShouldBe(TimeSpan.FromSeconds(5));
        TopUpSaga.Backoff(5, 60, 3).ShouldBe(TimeSpan.FromSeconds(20));
        TopUpSaga.Backoff(5, 60, 10).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Saga_ids_are_the_same_for_the_same_inputs_and_differ_otherwise()
    {
        var user = Guid.NewGuid();
        TopUpService.SagaId("topup", user, "key-1").ShouldBe(TopUpService.SagaId("topup", user, "key-1"));
        TopUpService.SagaId("topup", user, "key-1").ShouldNotBe(TopUpService.SagaId("topup", user, "key-2"));
        TopUpService.SagaId("topup", user, "key-1").ShouldNotBe(TopUpService.SagaId("merchant-payment", user, "key-1"));
        TopUpService.SagaId("topup", user, "key-1").Version.ShouldBe(8);
    }
}

public class FeePlanTests
{
    private static FeePlan Plan(string code) => FeePlans.ByCode(code)!;

    [Fact]
    public void The_fee_is_a_percentage_and_the_tax_16_percent_of_it()
    {
        var split = Plan("STANDARD").Split(100_000);

        split.ShouldBe(new FeeSplit(100_000, 1_000, 160, 98_840));
    }

    [Fact]
    public void The_fee_has_a_floor_and_a_ceiling()
    {
        Plan("STANDARD").Split(5_000).FeePaisa.ShouldBe(100);
        Plan("STANDARD").Split(50_000_000).FeePaisa.ShouldBe(50_000);
        Plan("FUEL").Split(50_000_000).FeePaisa.ShouldBe(20_000);
    }

    [Fact]
    public void Small_shops_pay_nothing_on_tiny_payments()
    {
        var split = Plan("KIRYANA").Split(100);

        split.ShouldBe(new FeeSplit(100, 1, 0, 99));
        Plan("KIRYANA").Split(99).FeePaisa.ShouldBe(0);
    }

    [Fact]
    public void Rounding_is_half_up_to_whole_paisa()
    {
        // 1% of 12,350 = 123.5 -> 124; 16% of 124 = 19.84 -> 20.
        Plan("STANDARD").Split(12_350).ShouldBe(new FeeSplit(12_350, 124, 20, 12_206));

        // 0.8% of 1,234,567 = 9,876.536 -> 9,877; 16% of it = 1,580.32 -> 1,580.
        Plan("FOOD").Split(1_234_567).ShouldBe(new FeeSplit(1_234_567, 9_877, 1_580, 1_223_110));
    }

    [Theory]
    [InlineData(MerchantRules.MinAmountPaisa)]
    [InlineData(7_777)]
    [InlineData(1_234_567)]
    [InlineData(50_000_000)]
    public void Fee_tax_and_net_always_add_up_to_the_amount(long amount)
    {
        foreach (var plan in FeePlans.All)
        {
            var split = plan.Split(amount);
            (split.FeePaisa + split.TaxPaisa + split.NetPaisa).ShouldBe(amount);
            split.NetPaisa.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void Every_category_starts_on_a_plan_that_exists() =>
        FeePlans.Categories.ShouldAllBe(category => FeePlans.ByCode(category.FeePlan) != null);
}

public class QrCodecTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-qr-signing-key-0123456789");

    private static readonly QrContent Content = new(
        Guid.Parse("0193f1c4-0000-7000-8000-0000000000aa"), "WPKM123456", "Ali Tea Stall", 25_000, "INV-42", new DateTimeOffset(2026, 11, 18, 10, 5, 0, TimeSpan.Zero));

    [Fact]
    public void A_payload_decodes_to_what_was_encoded()
    {
        var payload = QrCodec.Encode(Content, Key);

        payload.ShouldStartWith("WPKQR1.");
        QrCodec.Decode(payload, Key).ShouldBe(Content);
    }

    [Fact]
    public void A_payload_without_a_reference_decodes()
    {
        var content = Content with { Reference = null };

        QrCodec.Decode(QrCodec.Encode(content, Key), Key).ShouldBe(content);
    }

    [Fact]
    public void Another_key_does_not_decode_it() =>
        QrCodec.Decode(QrCodec.Encode(Content, Key), Encoding.UTF8.GetBytes("another-key-0123456789-0123456789")).ShouldBeNull();

    [Fact]
    public void A_changed_body_or_signature_does_not_decode()
    {
        var payload = QrCodec.Encode(Content, Key);
        var other = QrCodec.Encode(Content with { AmountPaisa = 100 }, Key);
        var parts = payload.Split('.');
        var otherParts = other.Split('.');

        QrCodec.Decode($"{parts[0]}.{otherParts[1]}.{parts[2]}", Key).ShouldBeNull();
        QrCodec.Decode($"{parts[0]}.{parts[1]}.{otherParts[2]}", Key).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("WPKQR2.abc.def")]
    [InlineData("WPKQR1.!!!.???")]
    [InlineData("WPKQR1.abc")]
    public void Garbage_does_not_decode(string? payload) => QrCodec.Decode(payload, Key).ShouldBeNull();
}

public class MerchantHelperTests
{
    [Fact]
    public void Merchant_codes_are_WPKM_and_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            MerchantService.CodeFor(Guid.CreateVersion7()).ShouldMatch("^WPKM[0-9]{6}$");
        }
    }

    [Fact]
    public void The_same_merchant_always_has_the_same_code()
    {
        var id = Guid.NewGuid();
        MerchantService.CodeFor(id).ShouldBe(MerchantService.CodeFor(id));
    }

    [Theory]
    [InlineData("+923001234567", "+92300•••4567")]
    [InlineData(null, null)]
    [InlineData("123", null)]
    public void The_merchant_sees_the_payers_number_masked(string? number, string? masked) => MerchantService.Mask(number).ShouldBe(masked);
}
