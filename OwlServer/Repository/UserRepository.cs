using MySqlConnector;
using OwlServer.Models;

namespace OwlServer.Repository;

/// <summary>SELECT-only access to u_info (dev plan §14.1 - server never writes admin accounts).</summary>
public sealed class UserRepository(MySqlConnectionFactory connectionFactory)
{
    public async Task<User?> FindByUsernameAsync(string uName, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = connectionFactory.CommandTimeoutSeconds;
        command.CommandText = "SELECT u_id, u_name, pw FROM u_info WHERE u_name = @u_name LIMIT 1;";
        command.Parameters.Add(new MySqlParameter("@u_name", uName));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new User
        {
            UId = reader.GetInt32("u_id"),
            UName = reader.GetString("u_name"),
            Pw = reader.GetString("pw")
        };
    }
}
