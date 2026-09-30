using EfCoreTransactions.Api.DTOs;
using EfCoreTransactions.Api.Services;
using EfCoreTransactions.Api.Transactions;
using Microsoft.EntityFrameworkCore;

namespace EfCoreTransactions.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class OrderServiceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task CreateOrder_WithoutExplicitTransaction_PersistsOrderInventoryAndPayment()
    {
        await fixture.ResetAsync();
        var laptopId = await fixture.GetProductIdAsync("Laptop");

        await using var dbContext = fixture.CreateContext();
        var response = await fixture.CreateService(dbContext).CreateOrderAsync(
            new CreateOrderRequest { ProductId = laptopId, Quantity = 2 },
            TransactionMode.WithoutExplicitTransaction,
            simulatePaymentFailure: false,
            CancellationToken.None);

        Assert.Equal(3000m, response.TotalAmount);
        Assert.Equal(8, response.RemainingStock);
        Assert.Equal("Completed", response.PaymentStatus);

        await using var verify = fixture.CreateContext();
        var order = await verify.Orders
            .Include(candidate => candidate.OrderItems)
            .Include(candidate => candidate.Payment)
            .SingleAsync();

        Assert.Equal(response.OrderId, order.Id);
        Assert.Equal(3000m, order.TotalAmount);
        var item = Assert.Single(order.OrderItems);
        Assert.Equal(laptopId, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(1500m, item.UnitPrice);
        Assert.NotNull(order.Payment);
        Assert.Equal(3000m, order.Payment.Amount);
        Assert.Equal("Completed", order.Payment.Status);

        var state = await fixture.ReadStateAsync("Laptop");
        Assert.Equal(8, state.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_WithoutExplicitTransaction_WhenPaymentFails_LeavesPartialChanges()
    {
        await fixture.ResetAsync();
        var laptopId = await fixture.GetProductIdAsync("Laptop");

        await using var dbContext = fixture.CreateContext();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateService(dbContext).CreateOrderAsync(
                new CreateOrderRequest { ProductId = laptopId, Quantity = 2 },
                TransactionMode.WithoutExplicitTransaction,
                simulatePaymentFailure: true,
                CancellationToken.None));

        Assert.Equal("Simulated payment failure.", exception.Message);

        // A new context reads committed SQL Server state, not the failed request's change tracker.
        var state = await fixture.ReadStateAsync("Laptop");
        Assert.Equal(1, state.OrderCount);
        Assert.Equal(1, state.OrderItemCount);
        Assert.Equal(0, state.PaymentCount);
        Assert.Equal(8, state.StockQuantity);
    }

    [Theory]
    [InlineData(TransactionMode.WithoutExplicitTransaction)]
    [InlineData(TransactionMode.WithExplicitTransaction)]
    public async Task CreateOrder_WhenPaymentFails_ThrowsInvalidOperationException(TransactionMode transactionMode)
    {
        await fixture.ResetAsync();
        var laptopId = await fixture.GetProductIdAsync("Laptop");

        await using var dbContext = fixture.CreateContext();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateService(dbContext).CreateOrderAsync(
                new CreateOrderRequest { ProductId = laptopId, Quantity = 1 },
                transactionMode,
                simulatePaymentFailure: true,
                CancellationToken.None));

        Assert.Equal("Simulated payment failure.", exception.Message);
    }

    [Theory]
    [InlineData(TransactionMode.WithoutExplicitTransaction)]
    [InlineData(TransactionMode.WithExplicitTransaction)]
    public async Task CreateOrder_WhenInventoryIsInsufficient_LeavesTheDatabaseUnchanged(TransactionMode transactionMode)
    {
        await fixture.ResetAsync();
        var laptopId = await fixture.GetProductIdAsync("Laptop");

        await using var dbContext = fixture.CreateContext();
        var exception = await Assert.ThrowsAsync<InsufficientInventoryException>(() =>
            fixture.CreateService(dbContext).CreateOrderAsync(
                new CreateOrderRequest { ProductId = laptopId, Quantity = 11 },
                transactionMode,
                simulatePaymentFailure: false,
                CancellationToken.None));

        Assert.Equal("Laptop", exception.ProductName);
        Assert.Equal(10, exception.Available);
        Assert.Equal(11, exception.Requested);

        var state = await fixture.ReadStateAsync("Laptop");
        Assert.Equal(0, state.OrderCount);
        Assert.Equal(0, state.OrderItemCount);
        Assert.Equal(0, state.PaymentCount);
        Assert.Equal(10, state.StockQuantity);
    }
}
