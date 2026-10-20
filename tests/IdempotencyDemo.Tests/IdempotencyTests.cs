using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StackExchange.Redis;

namespace IdempotencyDemo.Tests;

/// <summary>Aynı senaryolar her store implementasyonuna karşı çalışır.</summary>
public abstract class IdempotencyTestsBase
{
    private readonly WebApplicationFactory<Program> factory;

    protected IdempotencyTestsBase(WebApplicationFactory<Program> baseFactory, Dictionary<string, string?> settings)
    {
        // Her test sınıfı kendi geçici SQLite dosyasını kullanır (ödemeler + SQLite store'un key'leri).
        var db = Path.Combine(Path.GetTempPath(), $"idem-{Guid.NewGuid():N}.db");
        factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Db", $"Data Source={db};Pooling=False");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
    }

    /// <summary>Store'un dış bağımlılığı (ör. Redis) yoksa testler atlanır.</summary>
    protected virtual string? SkipReason => null;

    private void SkipIfUnavailable() => Skip.If(SkipReason is not null, SkipReason);

    private static HttpRequestMessage Post(string? key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = JsonContent.Create(body) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    private static async Task<int> CountPayments(HttpClient c) =>
        (await c.GetFromJsonAsync<List<object>>("/payments"))!.Count;

    [SkippableFact]
    public async Task Missing_key_returns_400()
    {
        SkipIfUnavailable();
        var c = factory.CreateClient();
        var res = await c.SendAsync(Post(null, new { amount = 10, currency = "TRY" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [SkippableFact]
    public async Task Same_key_same_body_creates_one_payment_and_replays_response()
    {
        SkipIfUnavailable();
        var c = factory.CreateClient();
        var before = await CountPayments(c);
        var key = Guid.NewGuid().ToString();
        var body = new { amount = 100, currency = "TRY" };

        var first = await c.SendAsync(Post(key, body));
        var second = await c.SendAsync(Post(key, body));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(before + 1, await CountPayments(c));
    }

    [SkippableFact]
    public async Task Same_key_different_body_returns_422()
    {
        SkipIfUnavailable();
        var c = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        await c.SendAsync(Post(key, new { amount = 1, currency = "TRY" }));
        var res = await c.SendAsync(Post(key, new { amount = 2, currency = "TRY" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [SkippableFact]
    public async Task Failed_request_leaves_no_payment_and_key_can_be_retried()
    {
        SkipIfUnavailable();
        var c = factory.CreateClient();
        var before = await CountPayments(c);
        var key = Guid.NewGuid().ToString();
        var body = new { amount = 75, currency = "FAIL" }; // endpoint ödemeyi yazar, sonra 500 döner

        var first = await c.SendAsync(Post(key, body));
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Equal(before, await CountPayments(c)); // ödeme geri alındı

        // Key serbest kaldı: tekrar denenebilir, 409 veya replay değil, işlem yeniden çalışır.
        var second = await c.SendAsync(Post(key, body));
        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(before, await CountPayments(c));
    }

    [SkippableFact]
    public async Task Parallel_requests_with_same_key_process_only_once()
    {
        SkipIfUnavailable();
        var c = factory.CreateClient();
        var before = await CountPayments(c);
        var key = Guid.NewGuid().ToString();
        var body = new { amount = 50, currency = "USD" };

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => c.SendAsync(Post(key, body))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains("Idempotent-Replayed")));
        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        Assert.Equal(before + 1, await CountPayments(c));
    }
}

public class SqliteIdempotencyTests(WebApplicationFactory<Program> f)
    : IdempotencyTestsBase(f, new() { ["Idempotency:Store"] = "Sqlite" }),
      IClassFixture<WebApplicationFactory<Program>>;

public class InMemoryIdempotencyTests(WebApplicationFactory<Program> f)
    : IdempotencyTestsBase(f, new() { ["Idempotency:Store"] = "InMemory" }),
      IClassFixture<WebApplicationFactory<Program>>;

public class RedisIdempotencyTests(WebApplicationFactory<Program> f)
    : IdempotencyTestsBase(f, new() { ["Idempotency:Store"] = "Redis", ["ConnectionStrings:Redis"] = RedisEndpoint }),
      IClassFixture<WebApplicationFactory<Program>>
{
    private const string RedisEndpoint = "localhost:6379,connectTimeout=1000,abortConnect=false";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var mux = ConnectionMultiplexer.Connect(RedisEndpoint);
            return mux.IsConnected;
        }
        catch { return false; }
    });

    protected override string? SkipReason =>
        Available.Value ? null : "Redis localhost:6379 üzerinde çalışmıyor (docker compose up -d).";
}
