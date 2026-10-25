using System.Security.Claims;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>
/// "Sign in with Claude": the Claude Code CLI on this computer does the actual sign-in (browser OAuth with the user's
/// Claude account). Once it reports a signed-in account, the app issues its own cookie for that account.
/// </summary>
[AllowAnonymous]
public sealed class AccountController(ClaudeAccountService account) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken cancellationToken)
    {
        var status = await account.GetStatusAsync(refresh: true, cancellationToken);
        return View(new LoginViewModel(status, account.IsLoginInProgress, SafeReturnUrl(returnUrl)));
    }

    /// <summary>Polled by the login page while the sign-in console is open.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var status = await account.GetStatusAsync(refresh: true, cancellationToken);
        return Json(new
        {
            cliFound = status.CliFound,
            loggedIn = status.LoggedIn,
            account = status.Account,
            loginInProgress = account.IsLoginInProgress
        });
    }

    /// <summary>Opens <c>claude auth login</c> in a console window on this computer.</summary>
    [HttpPost]
    public IActionResult StartLogin()
    {
        if (!account.StartLogin())
        {
            return BadRequest(new { error = "Claude Code CLI not found. Install it, or set Claude:CliPath in appsettings.json." });
        }
        return Json(new { started = true });
    }

    /// <summary>Signs in to this app as the Claude account the CLI is signed in with.</summary>
    [HttpPost]
    public async Task<IActionResult> Continue(string? returnUrl, CancellationToken cancellationToken)
    {
        var status = await account.GetStatusAsync(refresh: true, cancellationToken);
        if (!status.LoggedIn)
        {
            TempData["Error"] = "Claude is not signed in yet. Click \"Sign in with Claude\" and finish the sign-in in your browser.";
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, status.Email ?? status.AuthMethod ?? "Claude user"),
            new(ClaudeAccountService.AccountClaim, status.Account!)
        };
        if (status.Email is not null) claims.Add(new Claim(ClaimTypes.Email, status.Email));
        if (status.SubscriptionType is not null) claims.Add(new Claim("claude:subscription", status.SubscriptionType));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    /// <summary>Signs out of this app. With <paramref name="claude"/>, also signs Claude Code on this computer out.</summary>
    [HttpPost]
    public async Task<IActionResult> Logout(bool claude, CancellationToken cancellationToken)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (claude)
        {
            await account.LogoutAsync(cancellationToken);
        }
        return RedirectToAction(nameof(Login));
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
