using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>Sends SMS messages. Locally the message is written to the logs (visible in Seq) instead.</summary>
public interface ISmsSender
{
    Task SendAsync(PhoneNumber to, string message, CancellationToken cancellationToken);
}
