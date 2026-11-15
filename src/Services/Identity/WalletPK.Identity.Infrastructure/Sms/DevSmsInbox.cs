using System.Collections.Concurrent;

namespace WalletPK.Identity.Infrastructure.Sms;

/// <summary>A text message as the stand-in SMS gateway "sent" it.</summary>
/// <param name="To">E.164 number.</param>
/// <param name="Message">The full text, OTP included.</param>
/// <param name="SentAt">When.</param>
public sealed record DevSms(string To, string Message, DateTimeOffset SentAt);

/// <summary>
/// Development only: keeps the last few messages per number in memory so the web app can show the
/// OTP while you click through sign-up and login (GET /api/identity/dev/sms). Never filled outside
/// Development, and the endpoint is not even mapped there. Replaced by simulated SMS in M11.4.
/// </summary>
public sealed class DevSmsInbox(TimeProvider timeProvider)
{
    private const int PerNumber = 10;
    private const int MaxNumbers = 10_000;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<DevSms>> _messages = new(StringComparer.Ordinal);

    public void Add(string to, string message)
    {
        if (_messages.Count >= MaxNumbers && !_messages.ContainsKey(to))
        {
            _messages.Clear(); // A dev box that sent to 10,000 numbers can lose its old test messages.
        }

        var queue = _messages.GetOrAdd(to, _ => new ConcurrentQueue<DevSms>());
        queue.Enqueue(new DevSms(to, message, timeProvider.GetUtcNow()));
        while (queue.Count > PerNumber && queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>Messages to <paramref name="to"/>, newest first.</summary>
    public IReadOnlyList<DevSms> For(string to) =>
        _messages.TryGetValue(to, out var queue) ? [.. queue.Reverse()] : [];
}
