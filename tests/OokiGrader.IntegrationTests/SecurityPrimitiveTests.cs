using OokiGrader.Host.Common;
using OokiGrader.Host.Security;

namespace OokiGrader.IntegrationTests;

public sealed class SecurityPrimitiveTests
{
    [Fact]
    public void UlidGeneratorProducesCanonicalSortableLengthIdentifiers()
    {
        var generator = new UlidGenerator(TimeProvider.System);

        var id = generator.NewId();

        Assert.Equal(26, id.Length);
        Assert.InRange(id[0], '0', '7');
        Assert.All(
            id,
            character => Assert.Contains(
                character,
                "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public void SessionTokensAreOpaqueAndOnlyTheirHashesNeedPersistence()
    {
        var service = new SessionTokenService();

        var pair = service.Create();

        Assert.NotEqual(pair.SessionToken, pair.SessionTokenHash);
        Assert.NotEqual(pair.CsrfToken, pair.CsrfTokenHash);
        Assert.True(service.Verify(pair.SessionToken, pair.SessionTokenHash));
        Assert.True(service.Verify(pair.CsrfToken, pair.CsrfTokenHash));
        Assert.False(service.Verify(pair.SessionToken + "x", pair.SessionTokenHash));
    }

    [Fact]
    public void PasswordPolicyRequiresTwelveCharactersAndBlocksCommonValues()
    {
        Assert.NotEmpty(PasswordPolicy.Validate("short"));
        Assert.NotEmpty(PasswordPolicy.Validate("password1234"));
        Assert.Empty(PasswordPolicy.Validate("correct horse battery staple"));
    }

    [Fact]
    public async Task PasswordHasherRoundTripsAndRejectsWrongPassword()
    {
        var hasher = new PasswordHasher();
        const string password = "日本語も使える強いパスワード-2026";

        var encoded = await hasher.HashAsync(password);

        Assert.StartsWith("$argon2id$v=19$", encoded, StringComparison.Ordinal);
        Assert.True(await hasher.VerifyAsync(password, encoded));
        Assert.False(await hasher.VerifyAsync(password + "x", encoded));
    }
}
