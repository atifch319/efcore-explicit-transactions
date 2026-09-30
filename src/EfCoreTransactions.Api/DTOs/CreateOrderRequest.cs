using System.ComponentModel.DataAnnotations;

namespace EfCoreTransactions.Api.DTOs;

public sealed class CreateOrderRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; init; }

    [Range(1, 10_000)]
    public int Quantity { get; init; }

    public bool UseExplicitTransaction { get; init; }

    public bool SimulatePaymentFailure { get; init; }
}
