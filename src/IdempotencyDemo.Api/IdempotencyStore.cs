using System.Collections.Concurrent;

namespace IdempotencyDemo.Api;

public enum IdempotencyState { InProgress = 0, Completed = 1 }

public sealed class IdempotencyRecord
{
    public required string RequestHash { get; init; }
    public IdempotencyState State { get; set; } = IdempotencyState.InProgress;
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? ContentType { get; set; }
}

public interface IIdempotencyStore
{
    /// <summary>Atomik olarak kaydı ekler. Key zaten varsa false döner ve mevcut kaydı verir.</summary>
    bool TryBegin(string key, string requestHash, out IdempotencyRecord record);

    /// <summary>İşlem bitince yanıtı kaydeder.</summary>
    void Complete(string key, int statusCode, string? contentType, string? body);

    /// <summary>İşlem başarısız olursa key'i serbest bırakır, istemci tekrar deneyebilir.</summary>
    void Abandon(string key);
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new();

    public bool TryBegin(string key, string requestHash, out IdempotencyRecord record)
    {
        var created = new IdempotencyRecord { RequestHash = requestHash };
        record = _records.GetOrAdd(key, created);
        return ReferenceEquals(record, created);
    }

    public void Complete(string key, int statusCode, string? contentType, string? body)
    {
        var r = _records[key];
        r.StatusCode = statusCode;
        r.ContentType = contentType;
        r.ResponseBody = body;
        r.State = IdempotencyState.Completed;
    }

    public void Abandon(string key) => _records.TryRemove(key, out _);
}
