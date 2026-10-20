using System.Globalization;

namespace IdempotencyDemo.Api;

public record Payment(Guid Id, decimal Amount, string Currency);

/// <summary>Ödemeleri idempotency kaydıyla aynı transaction'a yazar.</summary>
public sealed class PaymentRepository(SqliteUnitOfWork uow)
{
    public Payment Add(decimal amount, string currency)
    {
        var payment = new Payment(Guid.NewGuid(), amount, currency);
        using var cmd = uow.CreateCommand(
            "INSERT INTO Payments (Id, Amount, Currency, CreatedAt) VALUES ($id, $a, $c, $t)");
        cmd.Parameters.AddWithValue("$id", payment.Id.ToString());
        cmd.Parameters.AddWithValue("$a", amount.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$c", currency);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return payment;
    }

    public List<Payment> All()
    {
        using var cmd = uow.CreateCommand("SELECT Id, Amount, Currency FROM Payments ORDER BY CreatedAt");
        using var r = cmd.ExecuteReader();
        var list = new List<Payment>();
        while (r.Read())
            list.Add(new Payment(Guid.Parse(r.GetString(0)),
                decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2)));
        return list;
    }
}
