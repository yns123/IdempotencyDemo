using System.Text.Json;
using StackExchange.Redis;

namespace IdempotencyDemo.Api;

/// <summary>
/// Key'leri Redis'te tutar. Atomiklik SET ... NX ile gelir: key yoksa yazar, varsa dokunmaz.
/// SQL'den farkı: TTL hazır gelir (EXPIRE), ama iş verisiyle aynı transaction'a girmez.
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const string Prefix = "idem:";

    private readonly IDatabase _db;

    public RedisIdempotencyStore(IConnectionMultiplexer redis) => _db = redis.GetDatabase();

    public bool TryBegin(string key, string requestHash, out IdempotencyRecord record)
    {
        while (true)
        {
            var fresh = new IdempotencyRecord { RequestHash = requestHash };
            // When.NotExists = SET NX. Aynı anda gelen isteklerden yalnızca biri true alır.
            if (_db.StringSet(Prefix + key, Serialize(fresh), Ttl, When.NotExists))
            {
                record = fresh;
                return true;
            }

            var json = _db.StringGet(Prefix + key);
            if (json.IsNullOrEmpty) continue; // arada silinmiş veya süresi dolmuş, tekrar dene
            record = JsonSerializer.Deserialize<IdempotencyRecord>((string)json!)!;
            return false;
        }
    }

    public void Complete(string key, int statusCode, string? contentType, string? body)
    {
        var json = _db.StringGet(Prefix + key);
        if (json.IsNullOrEmpty) return;
        var record = JsonSerializer.Deserialize<IdempotencyRecord>((string)json!)!;
        record.StatusCode = statusCode;
        record.ContentType = contentType;
        record.ResponseBody = body;
        record.State = IdempotencyState.Completed;
        _db.StringSet(Prefix + key, Serialize(record), expiry: null, keepTtl: true);
    }

    public void Abandon(string key) => _db.KeyDelete(Prefix + key);

    private static string Serialize(IdempotencyRecord r) => JsonSerializer.Serialize(r);
}
