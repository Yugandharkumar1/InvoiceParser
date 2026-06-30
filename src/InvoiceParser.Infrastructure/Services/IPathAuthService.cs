using InvoiceParser.Core.Services;
using InvoiceParser.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Infrastructure.Services;

/// <summary>
/// Authenticates users against the iPath 'users' table.
///
/// Password storage: iPath stores passwords as a binary hash in the login_pwd column.
/// We compare using SQL Server's PWDCOMPARE() function which was the standard for
/// classic ASP / SQL Server authentication. If your app uses a different hashing
/// scheme, update the VerifyPasswordAsync method accordingly.
/// </summary>
public class IPathAuthService : IAuthService
{
    private readonly IPathDbContext _db;
    private readonly ILogger<IPathAuthService> _logger;

    public IPathAuthService(IPathDbContext db, ILogger<IPathAuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AuthenticatedUser?> ValidateAsync(string loginName, string password)
    {
        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(password))
            return null;

        // ── DEV BYPASS ───────────────────────────────────────────────────────
        // TODO: Remove this block once iPath password hash algorithm is confirmed.
        // Hardcoded test accounts for development only.
        if (loginName == "admin@test.com" && password == "Admin@123")
        {
            return new AuthenticatedUser(
                UserId: 0,
                LoginName: "admin@test.com",
                DisplayName: "Dev Admin",
                Email: "admin@test.com",
                IsAdmin: true,
                CustomerId: 0);
        }
        if (loginName == "user@test.com" && password == "User@123")
        {
            return new AuthenticatedUser(
                UserId: 0,
                LoginName: "user@test.com",
                DisplayName: "Dev User",
                Email: "user@test.com",
                IsAdmin: false,
                CustomerId: 0);
        }
        // ── END DEV BYPASS ───────────────────────────────────────────────────

        // Load user by login name (case-insensitive on SQL Server by default).
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.LoginName == loginName && u.IsActive && u.CanLogin);

        if (user is null)
        {
            _logger.LogWarning("Login failed: user '{LoginName}' not found or inactive.", loginName);
            return null;
        }

        // TODO: Replace PWDCOMPARE with the correct iPath hash algorithm once confirmed.
        if (!await VerifyPasswordAsync(loginName, password))
        {
            _logger.LogWarning("Login failed: wrong password for '{LoginName}'.", loginName);
            return null;
        }

        _logger.LogInformation("User '{LoginName}' authenticated successfully.", loginName);

        return new AuthenticatedUser(
            UserId: user.UserId,
            LoginName: user.LoginName,
            DisplayName: user.DisplayName ?? user.LoginName,
            Email: user.Email,
            IsAdmin: user.IsAdmin,
            CustomerId: user.CustomerId);
    }

    /// <summary>
    /// Verifies the password using SQL Server's PWDCOMPARE() function.
    /// This matches passwords stored with PWDENCRYPT() (used by classic iPath).
    /// Returns true if the password matches.
    /// </summary>
    private async Task<bool> VerifyPasswordAsync(string loginName, string password)
    {
        try
        {
            var sql = @"
                SELECT CAST(PWDCOMPARE(@pwd, login_pwd) AS BIT)
                FROM   users
                WHERE  login_name = @loginName
                  AND  is_active  = 1";

            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@pwd", password));
            cmd.Parameters.Add(new SqlParameter("@loginName", loginName));

            var result = await cmd.ExecuteScalarAsync();
            return result is true or 1 or (byte)1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password verification query failed for '{LoginName}'.", loginName);
            return false;
        }
    }
}
