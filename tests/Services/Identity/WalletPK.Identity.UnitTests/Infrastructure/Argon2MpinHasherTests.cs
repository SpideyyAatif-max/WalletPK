using WalletPK.Identity.Infrastructure.Security;

namespace WalletPK.Identity.UnitTests.Infrastructure;

public class Argon2MpinHasherTests
{
    // Low cost keeps the tests fast; production uses the OWASP-recommended defaults.
    private static Argon2MpinHasher Hasher(string pepper = "unit-test-pepper-that-is-at-least-32-chars") =>
        new(new MpinHashingOptions { Pepper = pepper, MemoryKiB = 1024, Iterations = 1, Parallelism = 1 });

    [Fact]
    public void Hashes_use_the_self_describing_argon2id_format()
    {
        var hash = Hasher().Hash("40291");

        hash.ShouldStartWith("$argon2id$v=19$m=1024,t=1,p=1$");
        hash.Split('$').Length.ShouldBe(6);
        hash.ShouldNotContain("40291");
    }

    [Fact]
    public void The_right_mpin_verifies_and_a_wrong_one_does_not()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("40291");

        hasher.Verify("40291", hash).ShouldBeTrue();
        hasher.Verify("40292", hash).ShouldBeFalse();
    }

    [Fact]
    public void The_same_mpin_gets_a_different_hash_each_time()
    {
        var hasher = Hasher();

        hasher.Hash("40291").ShouldNotBe(hasher.Hash("40291"));
    }

    [Fact]
    public void Without_the_pepper_a_stolen_hash_cannot_be_verified()
    {
        var hash = Hasher().Hash("40291");

        Hasher("a-different-pepper-of-at-least-32-characters").Verify("40291", hash).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("$argon2id$v=19$m=x,t=1,p=1$AAAA$AAAA")]
    [InlineData("$bcrypt$v=19$m=1024,t=1,p=1$AAAA$AAAA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$not-base64!$AAAA")]
    public void Malformed_hashes_never_verify(string hash)
    {
        Hasher().Verify("40291", hash).ShouldBeFalse();
    }
}
