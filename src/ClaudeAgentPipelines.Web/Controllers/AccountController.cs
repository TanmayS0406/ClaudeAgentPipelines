using System.Security.Claims;
using ClaudeAgentPipelines.Billing;
using ClaudeAgentPipelines.Claude;
using ClaudeAgentPipelines.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

/// <summary>
/// Signing in to the app (user name and password, checked against the database) and connecting the Claude account the
/// pipelines run on. For the Claude part the Claude Code CLI on this computer does the actual sign-in.
/// </summary>
public sealed class AccountController(UserService users, ClaudeAccountService claudeAccount) : Controller
{
    public const string UserNameClaim = "username";

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true
            ? LocalRedirect(SafeReturnUrl(returnUrl))
            : View(new LoginForm { ReturnUrl = SafeReturnUrl(returnUrl) });

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(form);

        var user = await users.AuthenticateAsync(form.UserName, form.Password, cancellationToken);
        if (user is null)
        {
            await Task.Delay(400, cancellationToken); // slows down guessing
            form.Password = string.Empty;
            ModelState.AddModelError(string.Empty, "Wrong user name or password.");
            return View(form);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(UserNameClaim, user.UserName)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(SafeReturnUrl(form.ReturnUrl));
    }

    /// <summary>Signs out of this app. With <paramref name="claude"/>, also signs Claude Code on this computer out.</summary>
    [HttpPost]
    public async Task<IActionResult> Logout(bool claude, CancellationToken cancellationToken)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (claude)
        {
            await claudeAccount.LogoutAsync(cancellationToken);
        }
        return RedirectToAction(nameof(Login));
    }

    /// <summary>The Claude account the pipelines run on (the one Claude Code is signed in with on this computer).</summary>
    [HttpGet]
    public async Task<IActionResult> Claude(CancellationToken cancellationToken)
    {
        var status = await claudeAccount.GetStatusAsync(refresh: true, cancellationToken);
        return View(new ClaudeViewModel(status, claudeAccount.IsLoginInProgress));
    }

    /// <summary>Polled by the Claude page while the sign-in console is open.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var status = await claudeAccount.GetStatusAsync(refresh: true, cancellationToken);
        return Json(new
        {
            cliFound = status.CliFound,
            loggedIn = status.LoggedIn,
            account = status.Account,
            loginInProgress = claudeAccount.IsLoginInProgress
        });
    }

    /// <summary>Opens <c>claude auth login</c> in a console window on this computer.</summary>
    [HttpPost]
    public IActionResult StartLogin()
    {
        if (!claudeAccount.StartLogin())
        {
            return BadRequest(new { error = "Claude Code CLI not found. Install it, or set Claude:CliPath in appsettings.json." });
        }
        return Json(new { started = true });
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
