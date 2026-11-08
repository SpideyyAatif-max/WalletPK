using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.IntegrationTests.Infrastructure;

/// <summary>Records SMS messages instead of sending them, so tests can read OTP codes.</summary>
public sealed partial class CapturingSmsSender : ISmsSender
{
    private readonly ConcurrentQueue<(string To, string Message)> _messages = new();

    public Task SendAsync(PhoneNumber to, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(to);
        _messages.Enqueue((to.Value, message));
        return Task.CompletedTask;
    }

    /// <summary>The most recent OTP sent to a number in E.164 form (+923001234567).</summary>
    public string LatestCodeFor(string e164PhoneNumber)
    {
        var message = _messages.Where(sms => sms.To == e164PhoneNumber).Select(sms => sms.Message).LastOrDefault()
            ?? throw new InvalidOperationException($"No SMS was sent to {e164PhoneNumber}.");
        return SixDigits().Match(message).Value;
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
