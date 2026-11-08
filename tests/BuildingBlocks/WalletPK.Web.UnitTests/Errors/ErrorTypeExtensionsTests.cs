using WalletPK.Common.Results;
using WalletPK.Web.Errors;

namespace WalletPK.Web.UnitTests.Errors;

public class ErrorTypeExtensionsTests
{
    [Fact]
    public void Every_error_type_has_a_status_and_a_title()
    {
        foreach (var type in Enum.GetValues<ErrorType>())
        {
            type.ToStatusCode().ShouldBeInRange(400, 599);
            type.ToTitle().ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Business_rule_violations_are_422_not_400()
    {
        // 400 = the request itself is malformed; 422 = it is well-formed but breaks a rule (e.g. insufficient balance).
        ErrorType.BusinessRule.ToStatusCode().ShouldBe(422);
        ErrorType.Validation.ToStatusCode().ShouldBe(400);
    }
}
