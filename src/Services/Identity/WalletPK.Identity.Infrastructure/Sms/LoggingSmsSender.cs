using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Sms;

/// <summary>
/// Stand-in SMS gateway. In Development the full message (including the OTP) is logged, so you can
/// read codes in Seq while testing; in other environments only the masked number is logged, because
/// OTPs must never appear in production logs. A real SMS provider would replace this class.
/// </summary>
internal sealed partial class LoggingSmsSender(IHostEnvironment environment, ILogger<LoggingSmsSender> logger) : ISmsSender
{
    public Task SendAsync(PhoneNumber to, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(to);

        if (environment.IsDevelopment())
        {
            LogDevelopmentSms(logger, to.Value, message);
        }
        else
        {
            LogSmsSent(logger, to.Masked);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "[DEV SMS] To {PhoneNumber}: {Message}")]
    private static partial void LogDevelopmentSms(ILogger logger, string phoneNumber, string message);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "SMS sent to {PhoneNumber}")]
    private static partial void LogSmsSent(ILogger logger, string phoneNumber);
}
