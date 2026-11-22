namespace WalletPK.Notifications.Domain;

/// <summary>Where a notification can go (M11.4).</summary>
[Flags]
public enum Channels
{
    None = 0,

    /// <summary>The notification centre in the app, pushed live over SignalR. Every notification has it.</summary>
    InApp = 1,

    /// <summary>A text message to the user's mobile number (simulated SMS gateway).</summary>
    Sms = 2,

    /// <summary>An email, if the user gave an address (simulated email gateway).</summary>
    Email = 4,
}

/// <summary>The languages messages are written in (M11.3).</summary>
public static class Languages
{
    public const string English = "en";

    public const string Urdu = "ur";

    public static IReadOnlyList<string> All { get; } = [English, Urdu];

    public static bool IsKnown(string? language) => language is English or Urdu;
}

/// <summary>What a notification is about, for grouping in the notification centre.</summary>
public static class Categories
{
    public const string Money = "Money";
    public const string Payments = "Payments";
    public const string Requests = "Requests";
    public const string Bills = "Bills";
    public const string Security = "Security";
    public const string Account = "Account";
    public const string Agent = "Agent";
}

/// <summary>One message in both languages.</summary>
/// <param name="Title">A few words, for the notification centre and the email subject.</param>
/// <param name="Body">The message; also the SMS text.</param>
public sealed record Wording(string Title, string Body);

/// <summary>
/// A kind of notification: its channels and wording. Text holds placeholders such as <c>{amount}</c>,
/// filled from the notification's parameters when it is shown or sent (see <see cref="MessageRenderer"/>).
/// </summary>
/// <param name="Key">For example "money_received"; stored with each notification.</param>
/// <param name="Category">See <see cref="Categories"/>.</param>
/// <param name="Channels">Where it goes. The user's settings can turn SMS and email off, except for security messages.</param>
/// <param name="Security">Account safety messages: always sent by SMS (and email when there is an address), whatever the settings.</param>
/// <param name="English">The English wording.</param>
/// <param name="Urdu">The Urdu wording.</param>
public sealed record Template(string Key, string Category, Channels Channels, bool Security, Wording English, Wording Urdu)
{
    public Wording In(string language) => language == Languages.Urdu ? Urdu : English;
}
