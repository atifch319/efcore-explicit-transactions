namespace EfCoreTransactions.Api.Models;

public sealed class Order
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal TotalAmount { get; set; }

    public ICollection<OrderItem> OrderItems { get; set; } = [];

    public Payment? Payment { get; set; }
}
