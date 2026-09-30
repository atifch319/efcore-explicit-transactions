namespace EfCoreTransactions.Api.DTOs;

public sealed record OrderResponse(
    int OrderId,
    DateTime CreatedAt,
    decimal TotalAmount,
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    int RemainingStock,
    int PaymentId,
    string PaymentStatus);
