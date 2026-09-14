using OwlServer.Repository;
using OwlServer.Utils;

namespace OwlServer.Services;

public sealed record LoginOutcome(bool Success, int? UserId);

/// <summary>
/// Verifies WPF admin login against u_info (dev plan §13 "AuthenticationService").
/// Never compares plaintext passwords - u_info.pw is always a bcrypt hash, and the
/// plaintext password received over the wire is discarded right after verification
/// (dev plan §29 rule 8).
/// </summary>
public sealed class AuthenticationService(UserRepository userRepository)
{
    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct)
    {
        Models.User? user;
        try
        {
            user = await userRepository.FindByUsernameAsync(username, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A DB outage must fail the login, not drop the WPF connection
            // (dev plan §26 - DB errors are logged and isolated, not propagated).
            Logger.Error($"Login lookup for '{username}' failed (DB error)", ex);
            return new LoginOutcome(false, null);
        }

        if (user is null)
        {
            return new LoginOutcome(false, null);
        }

        bool valid;
        try
        {
            valid = BCrypt.Net.BCrypt.Verify(password, user.Pw);
        }
        catch (BCrypt.Net.SaltParseException ex)
        {
            Logger.Error($"u_info.pw for '{username}' is not a valid bcrypt hash", ex);
            valid = false;
        }

        return valid ? new LoginOutcome(true, user.UId) : new LoginOutcome(false, null);
    }
}
