using Microsoft.Data.Sqlite;

namespace IdempotencyDemo.Api;

/// <summary>
/// Key'leri veritabanında tutar. Atomiklik PRIMARY KEY (unique) kısıtından gelir.
/// İstek başına UnitOfWork kullanır: key kaydı, ödeme kaydıyla aynı transaction'dadır.
/// SQLite yazma kilidi transaction bitene kadar tutulduğu için aynı key ile gelen ikinci istek
/// birincinin commit'ini bekler, sonra tamamlanmış kaydı görüp replay alır.
/// </summary>
public sealed class SqliteIdempotencyStore(SqliteUnitOfWork uow) : IIdempotencyStore
{
    private const int SqliteConstraintViolation = 19; // SQLITE_CONSTRAINT

    public bool TryBegin(string key, string requestHash, out IdempotencyRecord record)
    {
        try
        {
            using var cmd = uow.CreateCommand(
                "INSERT INTO IdempotencyKeys (Key, RequestHash, State, CreatedAt) VALUES ($k, $h, 0, $t)");
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$h", requestHash);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            record = new IdempotencyRecord { RequestHash = requestHash };
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
        {
            // Key zaten var (commit edilmiş): mevcut kaydı oku.
            record = Read(key)!;
            return false;
        }
    }

    public void Complete(string key, int statusCode, string? contentType, string? body)
    {
        using var cmd = uow.CreateCommand(
            "UPDATE IdempotencyKeys SET State = 1, StatusCode = $s, ContentType = $c, ResponseBody = $b WHERE Key = $k");
        cmd.Parameters.AddWithValue("$s", statusCode);
        cmd.Parameters.AddWithValue("$c", (object?)contentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    // Transaction geri alınınca key de gider; ayrıca bir şey yapmaya gerek yok.
    public void Abandon(string key) { }

    private IdempotencyRecord? Read(string key)
    {
        using var cmd = uow.CreateCommand(
            "SELECT RequestHash, State, StatusCode, ContentType, ResponseBody FROM IdempotencyKeys WHERE Key = $k");
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new IdempotencyRecord
        {
            RequestHash = r.GetString(0),
            State = (IdempotencyState)r.GetInt32(1),
            StatusCode = r.IsDBNull(2) ? 0 : r.GetInt32(2),
            ContentType = r.IsDBNull(3) ? null : r.GetString(3),
            ResponseBody = r.IsDBNull(4) ? null : r.GetString(4),
        };
    }
}
