using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace OokiGrader.Host.Security;

public static class OokiAuthenticationDefaults
{
    public const string Scheme = "OokiSession";
    public const string ProductionCookieName = "__Host-OokiSession";
    public const string DevelopmentCookieName = "OokiSession-Development";
}

public sealed class OokiSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IStaffAuthenticationService authentication,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = GetCookieName(configuration);
        if (!Request.Cookies.TryGetValue(cookieName, out var token)
            || string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var staff = await authentication.ResolveAsync(token, Context.RequestAborted);
        if (staff is null)
        {
            return AuthenticateResult.Fail("Session is invalid or expired.");
        }

        Response.Headers["X-Session-Expires-At"] =
            staff.SessionExpiresAt.ToString("O");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, staff.Id),
            new(ClaimTypes.Name, staff.Username),
            new("display_name", staff.DisplayName),
            new(
                "must_change_password",
                staff.MustChangePassword ? "true" : "false"),
            new("session_hash", staff.SessionHash),
            new("session_expires_at", staff.SessionExpiresAt.ToString("O")),
        };
        claims.AddRange(staff.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    public static string GetCookieName(IConfiguration configuration) =>
        configuration.GetValue("Security:RequireSecureCookies", true)
            ? OokiAuthenticationDefaults.ProductionCookieName
            : OokiAuthenticationDefaults.DevelopmentCookieName;
}
