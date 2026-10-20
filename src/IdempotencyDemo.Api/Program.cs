using IdempotencyDemo.Api;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Ödemeler her zaman SQLite'ta. İstek başına tek connection + transaction (UnitOfWork).
var dbConn = builder.Configuration.GetConnectionString("Db") ?? "Data Source=app.db";
SqliteSchema.Ensure(dbConn);
builder.Services.AddSingleton(new DbOptions(dbConn));
builder.Services.AddScoped<SqliteUnitOfWork>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<SqliteUnitOfWork>());
builder.Services.AddScoped<PaymentRepository>();

// Idempotency:Store = Sqlite (varsayılan) | Redis | InMemory
var storeType = builder.Configuration["Idempotency:Store"] ?? "Sqlite";
switch (storeType)
{
    case "Redis":
        var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
        builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        break;
    case "InMemory":
        builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        break;
    default:
        // Key kaydı ödemeyle aynı transaction'da: scoped.
        builder.Services.AddScoped<IIdempotencyStore, SqliteIdempotencyStore>();
        break;
}

var app = builder.Build();

app.UseMiddleware<IdempotencyMiddleware>();

app.MapPost("/payments", async (CreatePaymentRequest req, PaymentRepository repo) =>
{
    await Task.Delay(300); // iş yapılıyormuş gibi: paralel istek senaryosunu görünür kılar
    var payment = repo.Add(req.Amount, req.Currency);

    // Demo: ödeme yazıldıktan sonra hata oluşursa transaction geri alınır, ödeme de key de kalmaz.
    if (req.Currency == "FAIL") return Results.StatusCode(500);

    return Results.Created($"/payments/{payment.Id}", payment);
});

app.MapGet("/payments", (PaymentRepository repo) => repo.All());

app.Run();

public record CreatePaymentRequest(decimal Amount, string Currency);

public partial class Program; // WebApplicationFactory için
