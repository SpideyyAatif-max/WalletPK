using WalletPK.Common.Results;

namespace WalletPK.Common.UnitTests.Results;

public class ResultTests
{
    private static readonly Error _insufficientBalance =
        Error.BusinessRule("wallet.insufficient_balance", "Balance is too low.");

    [Fact]
    public void Success_carries_a_value()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_carries_an_error_and_hides_the_value()
    {
        Result<int> result = _insufficientBalance;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(_insufficientBalance);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Non_generic_result_supports_success_and_failure()
    {
        Result.Success().IsSuccess.ShouldBeTrue();

        Result failed = _insufficientBalance;
        failed.IsFailure.ShouldBeTrue();
        failed.Error.Code.ShouldBe("wallet.insufficient_balance");
    }

    [Fact]
    public void Map_transforms_a_success_and_skips_a_failure()
    {
        Result.Success(10).Map(x => x * 2).Value.ShouldBe(20);

        Result.Failure<int>(_insufficientBalance).Map(x => x * 2).Error.ShouldBe(_insufficientBalance);
    }

    [Fact]
    public void Bind_short_circuits_on_the_first_failure()
    {
        var calls = 0;
        Result<int> Next(int x)
        {
            calls++;
            return x + 1;
        }

        var result = Result.Failure<int>(_insufficientBalance).Bind(Next);

        result.IsFailure.ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Fact]
    public void Ensure_turns_a_success_into_a_failure_when_the_rule_fails()
    {
        var tooSmall = Error.Validation("amount.too_small", "Minimum is Rs. 1.");

        Result.Success(0).Ensure(x => x > 0, tooSmall).Error.ShouldBe(tooSmall);
        Result.Success(5).Ensure(x => x > 0, tooSmall).Value.ShouldBe(5);
    }

    [Fact]
    public void Match_runs_the_right_branch()
    {
        Result.Success(1).Match(v => "ok", e => e.Code).ShouldBe("ok");
        Result.Failure<int>(_insufficientBalance).Match(v => "ok", e => e.Code).ShouldBe("wallet.insufficient_balance");
    }

    [Fact]
    public void Error_factories_set_the_right_type()
    {
        Error.Validation("c", "m").Type.ShouldBe(ErrorType.Validation);
        Error.NotFound("c", "m").Type.ShouldBe(ErrorType.NotFound);
        Error.Conflict("c", "m").Type.ShouldBe(ErrorType.Conflict);
        Error.Unauthorized("c", "m").Type.ShouldBe(ErrorType.Unauthorized);
        Error.Forbidden("c", "m").Type.ShouldBe(ErrorType.Forbidden);
        Error.BusinessRule("c", "m").Type.ShouldBe(ErrorType.BusinessRule);
        Error.Unavailable("c", "m").Type.ShouldBe(ErrorType.Unavailable);
        Error.Failure("c", "m").Type.ShouldBe(ErrorType.Failure);
        Error.TooManyRequests("c", "m").Type.ShouldBe(ErrorType.TooManyRequests);
    }

    [Fact]
    public void Error_requires_a_code()
    {
        Should.Throw<ArgumentException>(() => new Error(" ", "message", ErrorType.Failure));
    }
}
