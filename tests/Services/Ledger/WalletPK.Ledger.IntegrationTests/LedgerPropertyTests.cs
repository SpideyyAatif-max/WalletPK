using System.Net;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M3.12 property-based test against the real service: FsCheck generates random scenarios (transfers with
/// fees, holds placed, captured and released), which run five operations at a time in parallel. Whatever
/// succeeds or fails, money must be conserved to the paisa and every invariant check must pass.
/// </summary>
[Collection(LedgerCollection.Name)]
public class LedgerPropertyTests(LedgerApiFactory factory)
{
    private const int Customers = 4;
    private const int Parallelism = 5;

    public sealed record Step(string Kind, int From, int To, long Paisa, int HoldIndex, int CapturePercent);

    private static readonly Gen<Step> _step =
        from kind in Gen.Elements("transfer", "transfer", "transfer", "hold", "capture", "release")
        from source in Gen.Choose(0, Customers - 1)
        from target in Gen.Choose(0, Customers - 1)
        from paisa in Gen.Frequency((1, Gen.Choose(1, 99)), (20, Gen.Choose(100, 300_000)))
        from holdIndex in Gen.Choose(0, 20)
        from percent in Gen.Choose(1, 100)
        select new Step(kind, source, target, paisa, holdIndex, percent);

    private static readonly Gen<(int[] Funding, List<Step> Steps)> _scenario =
        from funding in Gen.ArrayOf(Gen.Choose(0, 5_000), Customers)
        from steps in Gen.ListOf(_step, 25)
        select (funding, steps);

    [Fact]
    public async Task Random_parallel_scenarios_conserve_money_and_keep_every_invariant()
    {
        foreach (var (funding, steps) in _scenario.Sample(10, 8))
        {
            await RunScenarioAsync(funding, steps);
        }
    }

    private async Task RunScenarioAsync(int[] funding, List<Step> steps)
    {
        var customers = new Customer[Customers];
        long cashedIn = 0;
        for (var i = 0; i < Customers; i++)
        {
            customers[i] = await factory.CustomerAsync(rupees: funding[i]);
            cashedIn += funding[i] * 100L;
        }

        var holds = new List<HoldId>();
        long charged = 0;   // fees and tax on successful transfers
        long captured = 0;  // money paid out of wallets by captured holds

        foreach (var batch in steps.Chunk(Parallelism))
        {
            var knownHolds = holds.ToArray();
            var outcomes = await Task.WhenAll(batch.Select(step => RunStepAsync(step, customers, knownHolds)));
            foreach (var outcome in outcomes)
            {
                charged += outcome.Charged;
                captured += outcome.Captured;
                if (outcome.NewHold is { } hold)
                {
                    holds.Add(hold);
                }
            }
        }

        // Money only left these four wallets as fees, tax or captured payments.
        var balances = await Task.WhenAll(customers.Select(customer => factory.BalanceAsync(customer.WalletId)));
        balances.Sum().ShouldBe(cashedIn - charged - captured, $"Scenario: funding [{string.Join(", ", funding)}], steps {string.Join(", ", steps)}");

        foreach (var customer in customers)
        {
            customer.Client.Dispose();
        }

        await factory.ShouldBeHealthyAsync();
    }

    private sealed record Outcome(long Charged = 0, long Captured = 0, HoldId? NewHold = null);

    private async Task<Outcome> RunStepAsync(Step step, Customer[] customers, HoldId[] knownHolds)
    {
        switch (step.Kind)
        {
            case "transfer" when step.From != step.To:
                using (var response = await customers[step.From].SendAsync(customers[step.To].Phone, step.Paisa, NewKey()))
                {
                    if (step.Paisa < 100)
                    {
                        // Below Rs. 1 is always refused, before any money or limit is touched.
                        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Cancel));
                        return new Outcome();
                    }

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Cancel));
                        return new Outcome();
                    }

                    var receipt = await JsonAsync(response);
                    return new Outcome(Charged: Paisa(receipt, "fee") + Paisa(receipt, "tax"));
                }

            case "hold":
                var placed = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().PlaceAsync(
                    new PlaceHoldCommand(customers[step.From].WalletId, Money.FromPaisa(step.Paisa), "Bill payment", null, TimeSpan.FromHours(1)), Cancel));
                return placed.IsSuccess ? new Outcome(NewHold: placed.Value.HoldId) : new Outcome();

            case "capture" when step.HoldIndex < knownHolds.Length:
                var holdId = knownHolds[step.HoldIndex];
                var hold = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>()
                    .Holds.FindAsync([holdId], Cancel).AsTask());
                var amount = Money.FromPaisa(Math.Max(1, hold!.Amount * step.CapturePercent / 100));
                var capture = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().CaptureAsync(
                    new CaptureHoldCommand(holdId, TransactionId.New(), amount, JournalType.BillPayment, "Bill payment",
                        [PostingLine.Credit(AccountRef.System(ChartOfAccounts.BillerSettlement), amount)]),
                    Cancel));
                return capture.IsSuccess && !capture.Value.AlreadyPosted ? new Outcome(Captured: amount.MinorUnits) : new Outcome();

            case "release" when step.HoldIndex < knownHolds.Length:
                await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().ReleaseAsync(knownHolds[step.HoldIndex], Cancel));
                return new Outcome();

            default:
                return new Outcome();
        }
    }
}
