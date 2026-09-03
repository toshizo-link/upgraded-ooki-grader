using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
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

    [Fact]
    public async Task AuthenticatedActivityRenewsThePersistentSessionCookie()
    {
        var expiresAt = new DateTimeOffset(
            2026,
            9,
            26,
            12,
            0,
            0,
            TimeSpan.Zero);
        var authentication = new StubStaffAuthenticationService(
            new AuthenticatedStaff(
                "01J00000000000000000000000",
                "teacher",
                "先生",
                ["teacher"],
                MustChangePassword: false,
                expiresAt,
                "session-hash"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequireSecureCookies"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IStaffAuthenticationService>(authentication);
        services
            .AddAuthentication(OokiAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, OokiSessionAuthenticationHandler>(
                OokiAuthenticationDefaults.Scheme,
                _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Request.Headers.Cookie =
            $"{OokiAuthenticationDefaults.DevelopmentCookieName}=session-token";

        var handlerProvider = provider.GetRequiredService<
            IAuthenticationHandlerProvider>();
        var handler = await handlerProvider.GetHandlerAsync(
            context,
            OokiAuthenticationDefaults.Scheme);
        var result = await Assert.IsAssignableFrom<IAuthenticationHandler>(handler)
            .AuthenticateAsync();

        Assert.True(result.Succeeded);
        var setCookie = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(setCookie);
        var refreshed = SetCookieHeaderValue.Parse(setCookie);
        Assert.Equal(
            OokiAuthenticationDefaults.DevelopmentCookieName,
            refreshed.Name.ToString());
        Assert.Equal("session-token", refreshed.Value.ToString());
        Assert.Equal(expiresAt, refreshed.Expires);
    }

    private sealed class StubStaffAuthenticationService(
        AuthenticatedStaff staff) : IStaffAuthenticationService
    {
        public Task<AuthenticatedStaff?> ResolveAsync(
            string sessionToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedStaff?>(
                sessionToken == "session-token" ? staff : null);

        public Task<LoginAttemptResult> LoginAsync(
            string username,
            string password,
            System.Net.IPAddress? sourceAddress,
            string? userAgent,
            string correlationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(
            string sessionToken,
            string reason,
            string correlationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> RotateCsrfAsync(
            string sessionToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ValidateCsrfAsync(
            string sessionToken,
            string csrfToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
