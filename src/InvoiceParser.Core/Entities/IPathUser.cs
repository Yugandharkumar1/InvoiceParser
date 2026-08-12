using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

/// <summary>
/// Read-only projection of the iPath 'users' table.
/// Used for authenticating against the existing corporate user directory.
/// </summary>
[Table("users")]
public class IPathUser
{
    [Key]
    [Column("user_id")]
    public int UserId { get; set; }

    [Column("user_name")]
    [StringLength(100)]
    public string? DisplayName { get; set; }

    [Column("login_name")]
    [StringLength(100)]
    public string LoginName { get; set; } = string.Empty;

    /// <summary>Password stored as a binary hash. Compared server-side via PWDCOMPARE.</summary>
    [Column("login_pwd")]
    public byte[]? LoginPassword { get; set; }

    [Column("user_email")]
    [StringLength(100)]
    public string? Email { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("vmanager_admin")]
    public bool IsAdmin { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("ipath_login")]
    public bool CanLogin { get; set; }
}
