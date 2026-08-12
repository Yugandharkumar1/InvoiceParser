using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceParser.Web.Controllers;

/// <summary>
/// Kept for backwards-compatibility. User management has moved to UsersController.
/// </summary>
[Authorize(Roles = "Admin")]
public class AccountController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Users");
}
