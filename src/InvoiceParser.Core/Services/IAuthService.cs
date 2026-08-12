namespace InvoiceParser.Core.Services;

public record AuthenticatedUser(
    int UserId,
    string LoginName,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    int CustomerId);

public interface IAuthService
{
    /// <summary>
    /// Validates the credentials against the iPath users table.
    /// Returns the authenticated user on success, or null on failure.
    /// </summary>
    Task<AuthenticatedUser?> ValidateAsync(string loginName, string password);
}
