using WalletPK.Notifications.Domain;

namespace WalletPK.Notifications.UnitTests;

/// <summary>M11.3: every message exists in English and Urdu and both say the same things.</summary>
public class TemplateTests
{
    public static TheoryData<string> Keys => [.. TemplateCatalog.All.Select(template => template.Key)];

    [Theory]
    [MemberData(nameof(Keys))]
    public void Both_languages_fill_the_same_placeholders(string key)
    {
        var template = TemplateCatalog.Get(key);

        Placeholders(template.Urdu).ShouldBe(Placeholders(template.English), ignoreOrder: true, key);
        template.English.Title.ShouldNotBeNullOrWhiteSpace();
        template.Urdu.Body.ShouldNotBeNullOrWhiteSpace();
        template.Channels.HasFlag(Channels.InApp).ShouldBeTrue();
    }

    [Fact]
    public void Every_phrase_is_translated_with_the_same_placeholders()
    {
        foreach (var (key, (english, urdu)) in Phrases.All)
        {
            MessageRenderer.PlaceholdersIn(urdu).ToHashSet().ShouldBe(MessageRenderer.PlaceholdersIn(english).ToHashSet(), ignoreOrder: true, key);
            urdu.ShouldNotBe(english, key);
        }
    }

    [Fact]
    public void Security_messages_go_by_SMS_and_email_and_there_is_one_template_per_key()
    {
        TemplateCatalog.All.Select(template => template.Key).ShouldBeUnique();
        TemplateCatalog.All.Where(template => template.Security).Select(template => template.Key)
            .ShouldBe([TemplateCatalog.NewDevice, TemplateCatalog.AccountFrozen, TemplateCatalog.AccountUnfrozen], ignoreOrder: true);
        TemplateCatalog.All.Where(template => template.Security)
            .ShouldAllBe(template => template.Channels == (Channels.InApp | Channels.Sms | Channels.Email));
    }

    [Fact]
    public void Every_journal_type_has_a_phrase()
    {
        string[] journalTypes =
            ["CashIn", "CashOut", "P2PTransfer", "BankTransfer", "BillPayment", "MobileTopup", "MerchantPayment", "Reversal", "Adjustment", "CommissionPayout", "FloatTransfer"];

        journalTypes.ShouldAllBe(type => Phrases.All.ContainsKey($"journal:{type}"));
    }

    private static HashSet<string> Placeholders(Wording wording) =>
        [.. MessageRenderer.PlaceholdersIn(wording.Title), .. MessageRenderer.PlaceholdersIn(wording.Body)];
}

/// <summary>Filling templates in the reader's language.</summary>
public class RendererTests
{
    private static readonly Dictionary<string, string> _received = new()
    {
        ["amount"] = MessageRenderer.Rupees(25_000),
        ["balance"] = MessageRenderer.Rupees(125_050),
        ["counterparty"] = "+92300*****67",
    };

    [Fact]
    public void A_payment_reads_naturally_in_English()
    {
        var wording = MessageRenderer.Render(TemplateCatalog.Get(TemplateCatalog.MoneyReceived), Languages.English, _received);

        wording.Title.ShouldBe("Money received");
        wording.Body.ShouldBe("You received Rs. 250.00 from +92300*****67. New balance: Rs. 1,250.50.");
    }

    [Fact]
    public void And_in_Urdu()
    {
        var wording = MessageRenderer.Render(TemplateCatalog.Get(TemplateCatalog.MoneyReceived), Languages.Urdu, _received);

        wording.Title.ShouldBe("رقم موصول ہوئی");
        wording.Body.ShouldBe("آپ کو +92300*****67 سے Rs. 250.00 موصول ہوئے۔ نیا بیلنس: Rs. 1,250.50۔");
    }

    [Fact]
    public void Phrases_are_translated_and_can_hold_placeholders_themselves()
    {
        var parameters = new Dictionary<string, string>
        {
            ["what"] = MessageRenderer.Phrase("failed:bill"),
            ["biller"] = "K-Electric",
            ["amount"] = MessageRenderer.Rupees(345_600),
            ["reason"] = "The biller is not answering.",
        };
        var template = TemplateCatalog.Get(TemplateCatalog.PaymentFailed);

        MessageRenderer.Render(template, Languages.English, parameters).Body
            .ShouldBe("Your K-Electric bill payment of Rs. 3,456.00 did not go through: The biller is not answering. Any money held for it is back in your wallet.");
        MessageRenderer.Render(template, Languages.Urdu, parameters).Body
            .ShouldStartWith("آپ کی ادائیگی مکمل نہیں ہو سکی (K-Electric کے بل کی Rs. 3,456.00 کی ادائیگی)۔");
    }

    [Fact]
    public void Dates_and_months_are_written_out_in_each_language_and_times_are_in_Pakistan_time()
    {
        var parameters = new Dictionary<string, string>
        {
            ["biller"] = "LESCO",
            ["name"] = "Home",
            ["month"] = MessageRenderer.Month("2026-11"),
            ["amount"] = MessageRenderer.Rupees(1_234_500),
            ["due"] = MessageRenderer.Date(new DateOnly(2026, 12, 5)),
        };
        var template = TemplateCatalog.Get(TemplateCatalog.BillDue);

        MessageRenderer.Render(template, Languages.English, parameters).Body.ShouldBe("Your LESCO bill (Home) for November 2026 is Rs. 12,345.00, due on 5 Dec 2026.");
        MessageRenderer.Render(template, Languages.Urdu, parameters).Body.ShouldContain("نومبر 2026");
        MessageRenderer.Render(template, Languages.Urdu, parameters).Body.ShouldContain("5 دسمبر 2026");

        var device = new Dictionary<string, string>
        {
            ["at"] = MessageRenderer.DateTime(new DateTimeOffset(2026, 11, 21, 19, 5, 0, TimeSpan.Zero)),
            ["until"] = MessageRenderer.DateTime(new DateTimeOffset(2026, 11, 22, 19, 5, 0, TimeSpan.Zero)),
        };
        MessageRenderer.Render(TemplateCatalog.Get(TemplateCatalog.NewDevice), Languages.English, device).Body
            .ShouldStartWith("A new device signed in to your account on 22 Nov 2026, 00:05. Payments from it are limited until 23 Nov 2026, 00:05.");
    }

    [Fact]
    public void A_missing_parameter_shows_as_its_placeholder()
    {
        MessageRenderer.Render(TemplateCatalog.Get(TemplateCatalog.Welcome), Languages.English, new Dictionary<string, string>()).Body
            .ShouldBe("Your wallet is ready. Your wallet number is your mobile number, {phone}.");
    }

    [Theory]
    [InlineData(0, "Rs. 0.00")]
    [InlineData(5, "Rs. 0.05")]
    [InlineData(125_050, "Rs. 1,250.50")]
    [InlineData(1_000_000_000, "Rs. 10,000,000.00")]
    public void Amounts_are_rupees_with_paisa(long paisa, string expected) => MessageRenderer.Rupees(paisa).ShouldBe(expected);
}
