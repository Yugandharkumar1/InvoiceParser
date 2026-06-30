using InvoiceParser.Infrastructure.Data;
using InvoiceParser.Web.Models.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceParser.Web.Controllers;

/// <summary>
/// Admin-only controller for browsing iPath users that have access to this application.
/// User creation and password management are handled in iPath itself.
/// </summary>
[Authorize(Roles = "Admin")]
public class UsersController : Controller
{
    private const int DefaultPageSize = 20;
    private readonly IPathDbContext _ipath;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IPathDbContext ipath, ILogger<UsersController> logger)
    {
        _ipath = ipath;
        _logger = logger;
    }

    // ── User grid ─────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? role, int page = 1)
    {
        page = Math.Max(1, page);

        var query = _ipath.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.CanLogin);

        // Search across name and login name.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u =>
                (u.DisplayName != null && u.DisplayName.ToLower().Contains(term)) ||
                u.LoginName.ToLower().Contains(term) ||
                (u.Email != null && u.Email.ToLower().Contains(term)));
        }

        // Role filter.
        if (role == "admin")
            query = query.Where(u => u.IsAdmin);
        else if (role == "user")
            query = query.Where(u => !u.IsAdmin);

        var total = await query.CountAsync();

        var users = await query
            .OrderBy(u => u.DisplayName)
            .Skip((page - 1) * DefaultPageSize)
            .Take(DefaultPageSize)
            .ToListAsync();

        var vm = new UserListViewModel
        {
            Users      = users,
            Page       = page,
            PageSize   = DefaultPageSize,
            TotalCount = total,
            Search     = search,
            RoleFilter = role,
        };

        return View(vm);
    }
}
