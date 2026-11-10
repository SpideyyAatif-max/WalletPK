using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Application.Concurrency;

/// <summary>
/// Three layers against double-spending (M3.6, M4.2):
/// <list type="number">
/// <item><b>Wallet locks (Redis):</b> every wallet a use case debits or credits is locked first, so
/// payments from one wallet run one at a time across all servers.</item>
/// <item><b>Row locks (PostgreSQL):</b> each attempt runs in one transaction and loads its accounts with
/// <c>SELECT ... FOR UPDATE</c> in ID order. System shards (fees, tax, bank float) are shared by every
/// payment and cannot be locked per wallet; their writers now queue for a few milliseconds instead of
/// colliding.</item>
/// <item><b>Row versions (xmin) and entry numbers:</b> the last line of defence. If an account still
/// changed between read and save, the attempt is retried from fresh data, up to <see cref="MaxAttempts"/> times.</item>
/// </list>
/// Plus the fencing token: a writer whose lock expired is refused (see <see cref="Account.ApplyFencingToken"/>).
/// </summary>
public sealed class WalletConcurrency(IAccountLockService locks, ILedgerUnitOfWork unitOfWork)
{
    public const int MaxAttempts = 3;

    public static Error WalletBusy { get; } =
        Error.Conflict("ledger.wallet_busy", "This wallet is busy with another payment. Please try again in a moment.");

    public static Error ConcurrentUpdate { get; } =
        Error.Conflict("ledger.concurrent_update", "Another payment changed this balance at the same moment. Please try again.");

    /// <summary>
    /// Locks <paramref name="walletIds"/>, then runs <paramref name="attempt"/> (load, change, save),
    /// retrying it when a save loses an optimistic-concurrency race.
    /// </summary>
    public async Task<Result<T>> RunAsync<T>(
        IReadOnlyCollection<AccountId> walletIds,
        Func<IReadOnlyDictionary<AccountId, long>, CancellationToken, Task<Result<T>>> attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(walletIds);
        ArgumentNullException.ThrowIfNull(attempt);

        await using var lease = await locks.TryAcquireAsync(walletIds, cancellationToken);
        if (lease is null)
        {
            return WalletBusy;
        }

        for (var attemptNumber = 1; ; attemptNumber++)
        {
            try
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
                var result = await attempt(lease.FencingTokens, cancellationToken);
                if (result.IsSuccess)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch (ConcurrencyConflictException) when (attemptNumber < MaxAttempts)
            {
                // The unit of work has discarded its stale state; back off a little and start again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25) * attemptNumber), cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                return ConcurrentUpdate;
            }
        }
    }

    /// <summary>Stamps each locked account with its lock's fencing token; fails if a lock was lost.</summary>
    public static Result ApplyFencingTokens(IEnumerable<Account> accounts, IReadOnlyDictionary<AccountId, long> tokens)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var account in accounts)
        {
            if (tokens.TryGetValue(account.Id, out var token))
            {
                var fenced = account.ApplyFencingToken(token);
                if (fenced.IsFailure)
                {
                    return fenced;
                }
            }
        }

        return Result.Success();
    }
}

/// <summary>Accounts loaded for one attempt, by ID.</summary>
internal static class AccountLookup
{
    public static async Task<Result<Dictionary<AccountId, Account>>> LoadAllAsync(
        IAccountRepository accounts,
        IReadOnlyCollection<AccountId> ids,
        CancellationToken cancellationToken)
    {
        var loaded = await accounts.GetManyAsync(ids, cancellationToken);
        var byId = loaded.ToDictionary(account => account.Id);
        foreach (var id in ids)
        {
            if (!byId.ContainsKey(id))
            {
                return LedgerErrors.AccountNotFound(id);
            }
        }

        return byId;
    }
}
