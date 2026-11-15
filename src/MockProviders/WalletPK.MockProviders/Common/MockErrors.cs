using WalletPK.Common.Results;

namespace WalletPK.MockProviders.Common;

/// <summary>Errors every provider can return.</summary>
public static class MockErrors
{
    public static Error DuplicateId(string what) =>
        Error.Conflict("provider.duplicate_id", $"This {what} was already used for a different request.");

    public static Error Invalid(string code, string message) => Error.Validation(code, message);

    public static Error Rejected(string code, string message) => Error.BusinessRule(code, message);
}
