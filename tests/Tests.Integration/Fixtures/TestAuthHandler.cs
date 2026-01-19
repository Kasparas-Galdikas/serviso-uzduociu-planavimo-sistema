using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Simple test auth handler that reads test headers to build a ClaimsPrincipal.
/// Defaults to manager role when no headers are provided.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public new const string Scheme = "TestAuth";

#pragma warning disable CS0618 // ISystemClock obsolete in newer auth APIs; acceptable for test shim
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock)
        : base(options, logger, encoder, clock)
#pragma warning restore CS0618
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Email", out var emailHeader) ||
            string.IsNullOrWhiteSpace(emailHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-Email"));
        }

        if (!Request.Headers.TryGetValue("X-Test-Role", out var roleHeader) ||
            string.IsNullOrWhiteSpace(roleHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-Role"));
        }

        var email = emailHeader.ToString();
        var role = roleHeader.ToString().ToLowerInvariant();
        if (role != "manager" && role != "technician")
        {
            return Task.FromResult(AuthenticateResult.Fail($"Unsupported role: {role}"));
        }

        var techIdClaim = Request.Headers.TryGetValue("X-Test-TechnicianId", out var techHeader)
            ? techHeader.ToString()
            : null;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role)
        };

        if (!string.IsNullOrWhiteSpace(techIdClaim))
        {
            claims.Add(new("technician_id", techIdClaim));
        }

        var identity = new ClaimsIdentity(claims, Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
