using System.Globalization;
using WalletPK.MockProviders.Banks;

namespace WalletPK.MockProviders.Tests.Infrastructure;

public static class TestAccounts
{
    /// <summary>A fresh valid IBAN at a bank ("HABB" = HBL), with the account number ending in <paramref name="lastFour"/>.</summary>
    public static string Iban(string ibanCode = "HABB", string lastFour = "1234") =>
        WalletPK.MockProviders.Banks.Iban.For(ibanCode, Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12", CultureInfo.InvariantCulture) + lastFour);

    /// <summary>A fresh 13-digit K-Electric consumer number ending in <paramref name="lastFour"/>.</summary>
    public static string Consumer(int length = 13, string lastFour = "5678") =>
        Random.Shared.NextInt64(0, (long)Math.Pow(10, length - 4)).ToString(new string('0', length - 4), CultureInfo.InvariantCulture) + lastFour;

    /// <summary>A fresh Jazz number (0300-...) ending in <paramref name="lastFour"/>.</summary>
    public static string JazzNumber(string lastFour = "4321") =>
        "0300" + Random.Shared.Next(0, 1000).ToString("D3", CultureInfo.InvariantCulture) + lastFour;

    public static Bank Hbl => BankDirectory.ByCode("HBL")!;
}
