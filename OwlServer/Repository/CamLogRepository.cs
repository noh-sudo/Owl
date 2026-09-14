using MySqlConnector;
using OwlServer.Models;

namespace OwlServer.Repository;

/// <summary>
/// INSERT/SELECT access to cam_log. The detection-event INSERT runs inside an
/// explicit transaction per dev plan §14.1 - "이미지 저장 이벤트 발생 시 관련
/// 데이터 INSERT는 MySQL Transaction으로 처리한다".
/// </summary>
public sealed class CamLogRepository(MySqlConnectionFactory connectionFactory)
{
    /// <summary>Inserts a cam_log row and returns the generated l_id. Caller must have
    /// already saved the JPEG to disk first (dev plan §16 - file before DB row).</summary>
    public async Task<int> InsertAsync(string category, string thumbnailPath, DateTime createdAt, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = connectionFactory.CommandTimeoutSeconds;
            command.CommandText = """
                INSERT INTO cam_log (category, thumbnail, created_at)
                VALUES (@category, @thumbnail, @created_at);
                SELECT LAST_INSERT_ID();
                """;
            command.Parameters.Add(new MySqlParameter("@category", category));
            command.Parameters.Add(new MySqlParameter("@thumbnail", thumbnailPath));
            command.Parameters.Add(new MySqlParameter("@created_at", createdAt));

            var generatedId = (ulong)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return checked((int)generatedId);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }
}
