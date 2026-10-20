using System.Security.Cryptography;
using System.Text;

namespace IdempotencyDemo.Api;

/// <summary>
/// [POST] isteklerinde Idempotency-Key header'ını zorunlu kılar ve tekrar eden istekleri
/// işi yeniden yapmadan, kayıtlı yanıtla cevaplar.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";

    // store ve uow istek başına çözülür. SQLite modunda ikisi aynı transaction'ı paylaşır.
    public async Task InvokeAsync(HttpContext ctx, IIdempotencyStore store, IUnitOfWork uow)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue(HeaderName, out var keyValues) || string.IsNullOrWhiteSpace(keyValues))
        {
            await WriteError(ctx, 400, $"'{HeaderName}' header'ı zorunludur.");
            return;
        }
        var key = keyValues.ToString();

        // Body'yi iki kez okuyabilmek için buffer'la, hash'ini al.
        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ctx.Request.Body.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(ms.ToArray()));

        if (!store.TryBegin(key, hash, out var record))
        {
            // Key daha önce görülmüş. Yazılacak bir şey yok: transaction'ı bırak, kilidi serbest bırak.
            uow.Rollback();
            if (record.RequestHash != hash)
            {
                await WriteError(ctx, 422, "Bu Idempotency-Key farklı bir istek gövdesiyle daha önce kullanıldı.");
                return;
            }
            if (record.State == IdempotencyState.InProgress)
            {
                await WriteError(ctx, 409, "Aynı key ile bir istek hâlâ işleniyor.");
                return;
            }

            // Tamamlanmış: kayıtlı yanıtı aynen tekrar dön.
            ctx.Response.StatusCode = record.StatusCode;
            ctx.Response.ContentType = record.ContentType;
            ctx.Response.Headers["Idempotent-Replayed"] = "true";
            if (record.ResponseBody is not null) await ctx.Response.WriteAsync(record.ResponseBody);
            return;
        }

        // İlk istek: yanıtı yakalayıp kaydet.
        var originalBody = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next(ctx);

            if (ctx.Response.StatusCode >= 500)
            {
                // Sunucu hatası kalıcı sonuç sayılmaz, tekrar denenebilsin.
                // Rollback: ödeme de key de geri alınır (SQLite). Redis'te key ayrıca silinir.
                store.Abandon(key);
                uow.Rollback();
            }
            else
            {
                store.Complete(key, ctx.Response.StatusCode, ctx.Response.ContentType,
                    Encoding.UTF8.GetString(buffer.ToArray()));
                // Commit, istemci yanıtı görmeden ÖNCE: ödeme + key birlikte kalıcı olur.
                uow.Commit();
            }
        }
        catch
        {
            store.Abandon(key);
            uow.Rollback();
            buffer.SetLength(0); // başarı gibi görünen yarım yanıt istemciye gitmesin
            throw;
        }
        finally
        {
            buffer.Position = 0;
            ctx.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody);
        }
    }

    private static Task WriteError(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}
