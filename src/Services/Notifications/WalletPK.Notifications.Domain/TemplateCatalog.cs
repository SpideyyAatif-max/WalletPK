namespace WalletPK.Notifications.Domain;

/// <summary>
/// Every notification WalletPK sends, in English and Urdu (M11.3). Placeholders are filled from the
/// notification's parameters; a parameter starting with "#" names a phrase in <see cref="Phrases"/>, so
/// it is translated too (see <see cref="MessageRenderer"/>).
/// </summary>
public static class TemplateCatalog
{
    public const string Welcome = "welcome";
    public const string NewDevice = "new_device";
    public const string KycUpgraded = "kyc_upgraded";
    public const string MoneyReceived = "money_received";
    public const string MoneySent = "money_sent";
    public const string WalletCredited = "wallet_credited";
    public const string WalletDebited = "wallet_debited";
    public const string PaymentFailed = "payment_failed";
    public const string RequestReceived = "request_received";
    public const string RequestPaid = "request_paid";
    public const string RequestDeclined = "request_declined";
    public const string BillDue = "bill_due";
    public const string AccountFrozen = "account_frozen";
    public const string AccountUnfrozen = "account_unfrozen";
    public const string AgentApproved = "agent_approved";
    public const string AgentSuspended = "agent_suspended";
    public const string AgentRejected = "agent_rejected";
    public const string AgentFloatLow = "agent_float_low";

    private const Channels InAppAndSms = Channels.InApp | Channels.Sms;
    private const Channels Everywhere = Channels.InApp | Channels.Sms | Channels.Email;

    public static IReadOnlyList<Template> All { get; } =
    [
        new(Welcome, Categories.Account, InAppAndSms, false,
            new("Welcome to WalletPK", "Your wallet is ready. Your wallet number is your mobile number, {phone}."),
            new("WalletPK میں خوش آمدید", "آپ کا والٹ تیار ہے۔ آپ کا والٹ نمبر آپ کا موبائل نمبر {phone} ہے۔")),

        new(NewDevice, Categories.Security, Everywhere, true,
            new("New device signed in", "A new device signed in to your account on {at}. Payments from it are limited until {until}. If this was not you, call our helpline right away."),
            new("نئی ڈیوائس سے لاگ اِن", "{at} کو ایک نئی ڈیوائس سے آپ کے اکاؤنٹ میں لاگ اِن کیا گیا۔ {until} تک اس ڈیوائس سے ادائیگیاں محدود رہیں گی۔ اگر یہ آپ نہیں تھے تو فوراً ہماری ہیلپ لائن پر رابطہ کریں۔")),

        new(KycUpgraded, Categories.Account, Channels.InApp | Channels.Email, false,
            new("Account upgraded", "Your account is now {tier}. Your new limits apply from now on."),
            new("اکاؤنٹ اپ گریڈ ہو گیا", "آپ کا اکاؤنٹ اب {tier} ہے۔ آپ کی نئی حدیں ابھی سے لاگو ہیں۔")),

        new(MoneyReceived, Categories.Money, InAppAndSms, false,
            new("Money received", "You received {amount} from {counterparty}. New balance: {balance}."),
            new("رقم موصول ہوئی", "آپ کو {counterparty} سے {amount} موصول ہوئے۔ نیا بیلنس: {balance}۔")),

        new(MoneySent, Categories.Money, InAppAndSms, false,
            new("Money sent", "You sent {amount} to {counterparty}. New balance: {balance}."),
            new("رقم بھیج دی گئی", "آپ نے {counterparty} کو {amount} بھیجے۔ نیا بیلنس: {balance}۔")),

        new(WalletCredited, Categories.Money, InAppAndSms, false,
            new("Money added", "{amount} added to your wallet ({what}). New balance: {balance}."),
            new("رقم جمع ہوئی", "آپ کے والٹ میں {amount} جمع ہوئے ({what})۔ نیا بیلنس: {balance}۔")),

        new(WalletDebited, Categories.Money, InAppAndSms, false,
            new("Payment made", "{amount} paid from your wallet ({what}). New balance: {balance}."),
            new("ادائیگی ہو گئی", "آپ کے والٹ سے {amount} ادا کیے گئے ({what})۔ نیا بیلنس: {balance}۔")),

        new(PaymentFailed, Categories.Payments, Channels.InApp, false,
            new("Payment not completed", "Your {what} did not go through: {reason} Any money held for it is back in your wallet."),
            new("ادائیگی مکمل نہیں ہوئی", "آپ کی ادائیگی مکمل نہیں ہو سکی ({what})۔ وجہ: {reason} اس کے لیے روکی گئی رقم آپ کے والٹ میں واپس آ گئی ہے۔")),

        new(RequestReceived, Categories.Requests, InAppAndSms, false,
            new("Money request", "{requester} has asked you for {amount}. Open WalletPK to pay or decline."),
            new("رقم کی درخواست", "{requester} نے آپ سے {amount} کی درخواست کی ہے۔ ادا کرنے یا مسترد کرنے کے لیے WalletPK کھولیں۔")),

        new(RequestPaid, Categories.Requests, Channels.InApp, false,
            new("Request paid", "{payer} paid your request for {amount}."),
            new("درخواست ادا ہو گئی", "{payer} نے آپ کی {amount} کی درخواست ادا کر دی۔")),

        new(RequestDeclined, Categories.Requests, Channels.InApp, false,
            new("Request declined", "{payer} declined your request for {amount}."),
            new("درخواست مسترد", "{payer} نے آپ کی {amount} کی درخواست مسترد کر دی۔")),

        new(BillDue, Categories.Bills, InAppAndSms, false,
            new("Bill due", "Your {biller} bill ({name}) for {month} is {amount}, due on {due}."),
            new("بل کی آخری تاریخ", "آپ کے {biller} کے بل ({name}) برائے {month} کی رقم {amount} ہے، جو {due} تک ادا کرنی ہے۔")),

        new(AccountFrozen, Categories.Security, Everywhere, true,
            new("Your wallet is on hold", "To protect your money we have put your wallet on hold, so payments are paused. Please call our helpline to restore it."),
            new("آپ کا والٹ روک دیا گیا ہے", "آپ کی رقم کے تحفظ کے لیے ہم نے آپ کا والٹ عارضی طور پر روک دیا ہے، اس لیے ادائیگیاں فی الحال بند ہیں۔ بحالی کے لیے ہماری ہیلپ لائن پر رابطہ کریں۔")),

        new(AccountUnfrozen, Categories.Security, Everywhere, true,
            new("Your wallet is active again", "Your wallet is active again and you can make payments. Thank you for your patience."),
            new("آپ کا والٹ دوبارہ فعال ہے", "آپ کا والٹ دوبارہ فعال ہو گیا ہے اور آپ ادائیگیاں کر سکتے ہیں۔ آپ کے تعاون کا شکریہ۔")),

        new(AgentApproved, Categories.Agent, Channels.InApp | Channels.Email, false,
            new("Agent account approved", "Your agent account {code} is approved and active. You can start serving customers."),
            new("ایجنٹ اکاؤنٹ منظور", "آپ کا ایجنٹ اکاؤنٹ {code} منظور اور فعال ہو گیا ہے۔ آپ صارفین کی خدمت شروع کر سکتے ہیں۔")),

        new(AgentSuspended, Categories.Agent, Channels.InApp | Channels.Email, false,
            new("Agent account suspended", "Your agent account {code} is suspended. Reason: {reason}"),
            new("ایجنٹ اکاؤنٹ معطل", "آپ کا ایجنٹ اکاؤنٹ {code} معطل کر دیا گیا ہے۔ وجہ: {reason}")),

        new(AgentRejected, Categories.Agent, Channels.InApp | Channels.Email, false,
            new("Agent application not approved", "Your agent application {code} was not approved. Reason: {reason}"),
            new("ایجنٹ درخواست منظور نہیں ہوئی", "آپ کی ایجنٹ درخواست {code} منظور نہیں ہوئی۔ وجہ: {reason}")),

        new(AgentFloatLow, Categories.Agent, InAppAndSms, false,
            new("Float running low", "Your float is {float}, below your alert level of {threshold}. Add float to keep serving customers."),
            new("فلوٹ کم ہو رہا ہے", "آپ کا فلوٹ {float} ہے، جو آپ کی الرٹ حد {threshold} سے کم ہے۔ صارفین کی خدمت جاری رکھنے کے لیے فلوٹ شامل کریں۔")),
    ];

    private static readonly Dictionary<string, Template> _byKey = All.ToDictionary(template => template.Key, StringComparer.Ordinal);

    public static Template? Find(string key) => _byKey.GetValueOrDefault(key);

    public static Template Get(string key) => Find(key) ?? throw new ArgumentException($"No notification template '{key}'.", nameof(key));
}

/// <summary>
/// Translated phrases that go inside templates: a parameter "#bill_payment" is replaced by the phrase in the
/// reader's language, and the phrase can hold placeholders of its own.
/// </summary>
public static class Phrases
{
    public static IReadOnlyDictionary<string, (string English, string Urdu)> All { get; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        // What a wallet entry was (wallet_credited, wallet_debited), by journal type.
        ["journal:CashIn"] = ("cash-in", "کیش اِن"),
        ["journal:CashOut"] = ("cash-out", "کیش آؤٹ"),
        ["journal:P2PTransfer"] = ("wallet transfer", "والٹ ٹرانسفر"),
        ["journal:BankTransfer"] = ("bank transfer", "بینک ٹرانسفر"),
        ["journal:BillPayment"] = ("bill payment", "بل کی ادائیگی"),
        ["journal:MobileTopup"] = ("mobile top-up", "موبائل ٹاپ اَپ"),
        ["journal:MerchantPayment"] = ("merchant payment", "مرچنٹ ادائیگی"),
        ["journal:Reversal"] = ("money returned", "رقم کی واپسی"),
        ["journal:Adjustment"] = ("correction by WalletPK", "WalletPK کی جانب سے درستگی"),
        ["journal:CommissionPayout"] = ("commission payout", "کمیشن کی ادائیگی"),
        ["journal:FloatTransfer"] = ("float transfer", "فلوٹ کی منتقلی"),

        // What failed (payment_failed).
        ["failed:p2p"] = ("transfer of {amount} to {counterparty}", "{counterparty} کو {amount} کی منتقلی"),
        ["failed:bank"] = ("bank transfer of {amount} to {bank}", "{bank} میں {amount} کا بینک ٹرانسفر"),
        ["failed:bill"] = ("{biller} bill payment of {amount}", "{biller} کے بل کی {amount} کی ادائیگی"),
        ["failed:topup"] = ("{network} top-up of {amount}", "{network} پر {amount} کا ٹاپ اَپ"),
        ["failed:merchant"] = ("payment of {amount} to a merchant", "مرچنٹ کو {amount} کی ادائیگی"),

        ["no_reason"] = ("none given.", "کوئی وجہ نہیں بتائی گئی۔"),
    };
}
