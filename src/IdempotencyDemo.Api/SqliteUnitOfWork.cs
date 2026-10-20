using Microsoft.Data.Sqlite;

namespace IdempotencyDemo.Api;

public interface IUnitOfWork
{
    void Commit();
    void Rollback();
}

public sealed record DbOptions(string ConnectionString);

/// <summary>
/// İstek başına (scoped) tek connection + tek transaction.
/// Idempotency kaydı ve iş verisi (ödeme) aynı transaction'a yazılır: ikisi birlikte commit olur ya da ikisi de geri alınır.
/// </summary>
public sealed class SqliteUnitOfWork(DbOptions options) : IUnitOfWork, IDisposable
{
    private SqliteConnection? _conn;
    private SqliteTransaction? _tx;

    /// <summary>Connection ve transaction ilk kullanımda (tembel) açılır.</summary>
    public SqliteCommand CreateCommand(string sql)
    {
        if (_conn is null)
        {
            _conn = new SqliteConnection(options.ConnectionString);
            _conn.Open();
        }
        _tx ??= _conn.BeginTransaction();

        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public void Commit()
    {
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
    }

    public void Rollback()
    {
        _tx?.Rollback();
        _tx?.Dispose();
        _tx = null;
    }

    // Commit edilmemiş transaction Dispose'da geri alınır.
    public void Dispose()
    {
        _tx?.Dispose();
        _conn?.Dispose();
    }
}

/// <summary>Uygulama açılırken tabloları oluşturur.</summary>
public static class SqliteSchema
{
    public static void Ensure(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS IdempotencyKeys (
                Key          TEXT PRIMARY KEY,
                RequestHash  TEXT NOT NULL,
                State        INTEGER NOT NULL,
                StatusCode   INTEGER NULL,
                ContentType  TEXT NULL,
                ResponseBody TEXT NULL,
                CreatedAt    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Payments (
                Id        TEXT PRIMARY KEY,
                Amount    TEXT NOT NULL,
                Currency  TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
