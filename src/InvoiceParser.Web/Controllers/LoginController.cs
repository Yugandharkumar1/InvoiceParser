using System.Security.Claims;
using InvoiceParser.Core.Services;
using InvoiceParser.Web.Models.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceParser.Web.Controllers;

/// <summary>
/// Handles sign-in and sign-out using iPath user credentials.
/// </summary>
[AllowAnonymous]
public class LoginController : Controller
{
    private readonly IAuthService _auth;
    private readonly ILogger<LoginController> _logger;

    public LoginController(IAuthService auth, ILogger<LoginController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Index(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Invoice");

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoginViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = await _auth.ValidateAsync(model.Email, model.Password);

        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name,           user.LoginName),
            new("DisplayName",             user.DisplayName),
            new("CustomerId",              user.CustomerId.ToString()),
        };

        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        claims.Add(new Claim(ClaimTypes.Role, "User"));

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                ExpiresUtc   = DateTimeOffset.UtcNow.Add(
                    model.RememberMe ? TimeSpan.FromDays(30) : TimeSpan.FromHours(8))
            });

        _logger.LogInformation("User '{LoginName}' signed in.", user.LoginName);

        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return RedirectToAction("Index", "Invoice");
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _logger.LogInformation("User signed out.");
        return RedirectToAction(nameof(Index));
    }

    // ── Access Denied ─────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult AccessDenied() => View();
}
