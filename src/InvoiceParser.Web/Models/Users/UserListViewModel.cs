using InvoiceParser.Core.Entities;

namespace InvoiceParser.Web.Models.Users;

public class UserListViewModel
{
    public List<IPathUser> Users { get; set; } = new();

    // ── Pagination ────────────────────────────────────────────────────────────
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasPrev => Page > 1;
    public bool HasNext => Page < TotalPages;

    // ── Filters ───────────────────────────────────────────────────────────────
    public string? Search { get; set; }
    public string? RoleFilter { get; set; }   // "admin" | "user" | null = all
}
