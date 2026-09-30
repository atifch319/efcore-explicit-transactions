using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EfCoreTransactions.Api.Data;
using EfCoreTransactions.Api.DTOs;
using EfCoreTransactions.Api.Transactions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EfCoreTransactions.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class TransactionTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private OrdersApiFactory _factory = null!;

    public TransactionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new OrdersApiFactory(_fixture.ConnectionString);

        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SqlServerFixture.DatabaseName, dbContext.Database.GetDbConnection().Database);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task CreateOrder_WithExplicitTransaction_PersistsOrderInventoryAndPayment()
    {
        await _fixture.ResetAsync();
        var laptopId = await _fixture.GetProductIdAsync("Laptop");

        await using var dbContext = _fixture.CreateContext();
        var response = await _fixture.CreateService(dbContext).CreateOrderAsync(
            new CreateOrderRequest { ProductId = laptopId, Quantity = 2 },
            TransactionMode.WithExplicitTransaction,
            simulatePaymentFailure: false,
            CancellationToken.None);

        Assert.Equal(3000m, response.TotalAmount);
        Assert.Equal(8, response.RemainingStock);
        Assert.True(response.PaymentId > 0);

        await using var verify = _fixture.CreateContext();
        var order = await verify.Orders
            .Include(candidate => candidate.OrderItems)
            .Include(candidate => candidate.Payment)
            .SingleAsync();

        Assert.Equal(response.OrderId, order.Id);
        var item = Assert.Single(order.OrderItems);
        Assert.Equal(2, item.Quantity);
        Assert.NotNull(order.Payment);
        Assert.Equal(order.Id, order.Payment.OrderId);

        var state = await _fixture.ReadStateAsync("Laptop");
        Assert.Equal(1, state.OrderCount);
        Assert.Equal(1, state.OrderItemCount);
        Assert.Equal(1, state.PaymentCount);
        Assert.Equal(8, state.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_WithExplicitTransaction_WhenPaymentFails_RollsBackEveryChange()
    {
        await _fixture.ResetAsync();
        var laptopId = await _fixture.GetProductIdAsync("Laptop");

        await using var dbContext = _fixture.CreateContext();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.CreateService(dbContext).CreateOrderAsync(
                new CreateOrderRequest { ProductId = laptopId, Quantity = 2 },
                TransactionMode.WithExplicitTransaction,
                simulatePaymentFailure: true,
                CancellationToken.None));

        Assert.Equal("Simulated payment failure.", exception.Message);

        var state = await _fixture.ReadStateAsync("Laptop");
        Assert.Equal(0, state.OrderCount);
        Assert.Equal(0, state.OrderItemCount);
        Assert.Equal(0, state.PaymentCount);
        Assert.Equal(10, state.StockQuantity);
    }

    [Fact]
    public async Task PostOrder_WithExplicitTransaction_ReturnsCreatedAndPersists()
    {
        await _fixture.ResetAsync();
        var laptopId = await _fixture.GetProductIdAsync("Laptop");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new CreateOrderRequest
            {
                ProductId = laptopId,
                Quantity = 2,
                UseExplicitTransaction = true,
                SimulatePaymentFailure = false
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(3000m, body.TotalAmount);
        Assert.Equal(8, body.RemainingStock);
        Assert.Equal("Completed", body.PaymentStatus);

        var state = await _fixture.ReadStateAsync("Laptop");
        Assert.Equal(1, state.OrderCount);
        Assert.Equal(1, state.OrderItemCount);
        Assert.Equal(1, state.PaymentCount);
        Assert.Equal(8, state.StockQuantity);
    }

    [Fact]
    public async Task PostOrder_WithoutExplicitTransaction_WhenPaymentFails_ReturnsProblemAndLeavesPartialChanges()
    {
        await _fixture.ResetAsync();
        var laptopId = await _fixture.GetProductIdAsync("Laptop");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new CreateOrderRequest
            {
                ProductId = laptopId,
                Quantity = 2,
                UseExplicitTransaction = false,
                SimulatePaymentFailure = true
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("The order workflow failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Simulated payment failure.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at EfCoreTransactions", body, StringComparison.Ordinal);

        var state = await _fixture.ReadStateAsync("Laptop");
        Assert.Equal(1, state.OrderCount);
        Assert.Equal(1, state.OrderItemCount);
        Assert.Equal(0, state.PaymentCount);
        Assert.Equal(8, state.StockQuantity);
    }

    [Fact]
    public async Task PostOrder_WithExplicitTransaction_WhenPaymentFails_ReturnsProblemAndRollsBack()
    {
        await _fixture.ResetAsync();
        var laptopId = await _fixture.GetProductIdAsync("Laptop");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new CreateOrderRequest
            {
                ProductId = laptopId,
                Quantity = 2,
                UseExplicitTransaction = true,
                SimulatePaymentFailure = true
            });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("The order workflow failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Simulated payment failure.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at EfCoreTransactions", body, StringComparison.Ordinal);

        var state = await _fixture.ReadStateAsync("Laptop");
        Assert.Equal(0, state.OrderCount);
        Assert.Equal(0, state.OrderItemCount);
        Assert.Equal(0, state.PaymentCount);
        Assert.Equal(10, state.StockQuantity);
    }
}

public sealed class OrdersApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var descriptors = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>)
                    || descriptor.ServiceType == typeof(DbContextOptions)
                    || descriptor.ServiceType == typeof(AppDbContext)
                    || (descriptor.ServiceType.IsGenericType
                        && descriptor.ServiceType.GenericTypeArguments.Contains(typeof(AppDbContext))
                        && descriptor.ServiceType.Name.Contains("DbContextOptions", StringComparison.Ordinal)))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
        });
    }
}
