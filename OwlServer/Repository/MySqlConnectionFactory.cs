using Microsoft.Extensions.Options;
using MySqlConnector;
using OwlServer.Config;

namespace OwlServer.Repository;

/// <summary>
/// Wraps a MySqlDataSource (MySqlConnector's pooled-connection entry point, see dev
/// plan §29 rule 9 - "DB connection은 connection pooling을 고려한다"). The server
/// only ever SELECTs/INSERTs against tables that are assumed to already exist
/// (dev plan §14.1) - it never creates a database or table.
/// </summary>
public sealed class MySqlConnectionFactory : IAsyncDisposable
{
    private readonly MySqlDataSource _dataSource;

    public int CommandTimeoutSeconds { get; }

    public MySqlConnectionFactory(IOptions<ServerSettings> settings)
    {
        var mysql = settings.Value.MySql;
        CommandTimeoutSeconds = mysql.CommandTimeoutSeconds;

        var connectionStringBuilder = new MySqlConnectionStringBuilder
        {
            Server = mysql.Host,
            Port = (uint)mysql.Port,
            Database = mysql.Database,
            UserID = mysql.UserId,
            Password = mysql.Password,
            SslMode = Enum.Parse<MySqlSslMode>(mysql.SslMode, ignoreCase: true)
        };

        var builder = new MySqlDataSourceBuilder(connectionStringBuilder.ConnectionString);
        _dataSource = builder.Build();
    }

    public ValueTask<MySqlConnection> OpenConnectionAsync(CancellationToken ct) =>
        _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
