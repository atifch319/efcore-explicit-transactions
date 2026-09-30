using System.Data;
using EfCoreTransactions.Api.Data;
using EfCoreTransactions.Api.DTOs;
using EfCoreTransactions.Api.Models;
using EfCoreTransactions.Api.Transactions;
using Microsoft.EntityFrameworkCore;

namespace EfCoreTransactions.Api.Services;

public sealed class OrderService(AppDbContext dbContext)
{
    public Task<OrderResponse> CreateOrderAsync(
        CreateOrderRequest request,
        TransactionMode transactionMode,
        bool simulatePaymentFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return transactionMode switch
        {
            TransactionMode.WithoutExplicitTransaction => CreateWithoutExplicitTransactionAsync(
                request,
                simulatePaymentFailure,
                cancellationToken),
            TransactionMode.WithExplicitTransaction => CreateWithExplicitTransactionAsync(
                request,
                simulatePaymentFailure,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(transactionMode), transactionMode, "Unsupported transaction mode.")
        };
    }

    /// <summary>
    /// Each <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call is transactional
    /// for the changes included in that call, and each call commits on its own.
    /// Nothing here joins those calls into one business transaction.
    /// </summary>
    private async Task<OrderResponse> CreateWithoutExplicitTransactionAsync(
        CreateOrderRequest request,
        bool simulatePaymentFailure,
        CancellationToken cancellationToken)
    {
        var product = await GetProductAsync(request.ProductId, cancellationToken);
        EnsureSufficientStock(product, request.Quantity);

        var order = CreateOrder(product, request.Quantity);
        dbContext.Orders.Add(order);

        // SaveChanges() #1 commits the order and its items immediately.
        await dbContext.SaveChangesAsync(cancellationToken);

        product.StockQuantity -= request.Quantity;

        // SaveChanges() #2 commits the inventory update as a separate transaction.
        // The order above is already durable, even if a later step fails.
        await dbContext.SaveChangesAsync(cancellationToken);

        if (simulatePaymentFailure)
        {
            throw new InvalidOperationException("Simulated payment failure.");
        }

        var payment = CreatePayment(order);
        dbContext.Payments.Add(payment);

        // SaveChanges() #3 commits the payment. It cannot roll back the earlier calls.
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(order, product, payment);
    }

    /// <summary>
    /// The same three <c>SaveChanges()</c> calls run inside one explicit database transaction.
    /// They enlist in that transaction and stay uncommitted until <c>CommitAsync</c>.
    /// A failure rolls every one of them back.
    /// </summary>
    private async Task<OrderResponse> CreateWithExplicitTransactionAsync(
        CreateOrderRequest request,
        bool simulatePaymentFailure,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var product = await GetProductAsync(request.ProductId, cancellationToken);
            EnsureSufficientStock(product, request.Quantity);

            var order = CreateOrder(product, request.Quantity);
            dbContext.Orders.Add(order);

            // Participates in the open transaction. This does not commit it.
            await dbContext.SaveChangesAsync(cancellationToken);

            product.StockQuantity -= request.Quantity;

            // Also participates in the open transaction. Inventory is not durable yet.
            await dbContext.SaveChangesAsync(cancellationToken);

            if (simulatePaymentFailure)
            {
                throw new InvalidOperationException("Simulated payment failure.");
            }

            var payment = CreatePayment(order);
            dbContext.Payments.Add(payment);
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ToResponse(order, product, payment);
        }
        catch
        {
            // CancellationToken.None so a canceled request can still undo the transaction.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Product> GetProductAsync(int productId, CancellationToken cancellationToken)
    {
        return await dbContext.Products.SingleOrDefaultAsync(product => product.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException($"Product {productId} was not found.");
    }

    private static void EnsureSufficientStock(Product product, int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (product.StockQuantity < quantity)
        {
            throw new InsufficientInventoryException(product.Name, product.StockQuantity, quantity);
        }
    }

    private static Order CreateOrder(Product product, int quantity)
    {
        var unitPrice = product.Price;

        return new Order
        {
            CreatedAt = DateTime.UtcNow,
            TotalAmount = unitPrice * quantity,
            OrderItems =
            [
                new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = quantity,
                    UnitPrice = unitPrice
                }
            ]
        };
    }

    private static Payment CreatePayment(Order order)
    {
        return new Payment
        {
            OrderId = order.Id,
            Amount = order.TotalAmount,
            Status = "Completed",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static OrderResponse ToResponse(Order order, Product product, Payment payment)
    {
        var item = order.OrderItems.Single();

        return new OrderResponse(
            order.Id,
            order.CreatedAt,
            order.TotalAmount,
            product.Id,
            product.Name,
            item.Quantity,
            item.UnitPrice,
            product.StockQuantity,
            payment.Id,
            payment.Status);
    }
}
