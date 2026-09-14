using MySqlConnector;

namespace OwlServer.Repository;

/// <summary>INSERT access to shoot_log - the operator's approve/stop decision audit trail.</summary>
public sealed class ShootLogRepository(MySqlConnectionFactory connectionFactory)
{
    /// <summary>l_id may be null: a decision is not required to reference a cam_log row
    /// (dev plan §14 - "l_id는 상황에 따라 NULL을 허용할 수 있다").</summary>
    public async Task<int> InsertAsync(int uId, int? lId, bool isShoot, DateTime createdAt, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = connectionFactory.CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO shoot_log (u_id, l_id, is_shoot, created_at)
            VALUES (@u_id, @l_id, @is_shoot, @created_at);
            SELECT LAST_INSERT_ID();
            """;
        command.Parameters.Add(new MySqlParameter("@u_id", uId));
        command.Parameters.Add(new MySqlParameter("@l_id", (object?)lId ?? DBNull.Value));
        command.Parameters.Add(new MySqlParameter("@is_shoot", isShoot));
        command.Parameters.Add(new MySqlParameter("@created_at", createdAt));

        var generatedId = (ulong)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return checked((int)generatedId);
    }
}
