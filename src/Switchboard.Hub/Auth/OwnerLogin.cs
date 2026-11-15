using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Switchboard.Core.Security;

namespace Switchboard.Hub.Auth;

public sealed record LoginRequest(string? Password, bool Remember = false);

/// <summary>
/// The owner's web login: one password (no username, there is one owner) and a cookie.
/// </summary>
public static class OwnerLogin
{
    public const string RateLimitPolicy = "login";

    // Ties every cookie to the current password hash. Changing the password (./sb passwd and a
    // hub restart) changes the stamp, so every existing cookie stops working at once.
    private const string StampClaim = "pw_stamp";

    public static void AddOwnerLogin(this IServiceCollection services, HubOptions options)
    {
        // The cookie is encrypted with ASP.NET Core Data Protection keys. Keeping them on disk
        // means a hub restart doesn't sign everyone out.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.StateDir, "keys")))
            .SetApplicationName("switchboard");

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "sb_auth";
            // JavaScript can't read it, so an injected script can't steal it.
            o.Cookie.HttpOnly = true;
            // The browser sends it only on requests that start from our own pages, which
            // blocks most cross-site request forgery (CSRF).
            o.Cookie.SameSite = SameSiteMode.Strict;
            // Secure (HTTPS-only) whenever the request came over HTTPS (Tailscale).
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // Without "remember me": a browser-session cookie that expires after 12 hours
            // without use. Phones rarely close their browser, so the server-side expiry matters.
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;

            // The API answers 401 instead of redirecting to a login page; the UI handles that.
            o.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = ctx =>
            {
                var stamp = ctx.Principal?.FindFirstValue(StampClaim);
                if (options.OwnerPasswordHash is null || stamp != Stamp(options.OwnerPasswordHash))
                {
                    ctx.RejectPrincipal();
                }

                return Task.CompletedTask;
            };
        });
    }

    public static void MapOwnerLogin(this WebApplication app, HubOptions options)
    {
        app.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx) =>
        {
            if (options.OwnerPasswordHash is null)
            {
                return Results.Json(new { error = "No password is set. Run ./sb passwd, then restart the hub." }, statusCode: 503);
            }

            if (!PasswordHash.Verify(req.Password ?? "", options.OwnerPasswordHash))
            {
                return Results.Json(new { error = "Wrong password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "owner"), new Claim(StampClaim, Stamp(options.OwnerPasswordHash))],
                CookieAuthenticationDefaults.AuthenticationScheme);

            // "Remember me": a cookie that survives closing the browser, valid for 30 days.
            var properties = req.Remember
                ? new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30), AllowRefresh = false }
                : new AuthenticationProperties { IsPersistent = false };
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
            return Results.Ok(new { ok = true });
        })
        // At most 5 tries a minute per client address, so the password can't be guessed by
        // brute force. Each try also costs ~0.3s of PBKDF2 by design.
        .RequireRateLimiting(RateLimitPolicy);

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });
    }

    private static string Stamp(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];
}
