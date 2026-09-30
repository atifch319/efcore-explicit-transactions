using EfCoreTransactions.Api.Data;
using EfCoreTransactions.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EfCoreTransactions.Tests;

/// <summary>
/// SQL Server fixture. Transaction behavior is verified against SQL Server,
/// not the EF Core InMemory provider.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string DatabaseName = "EfCoreTransactionsTestsDb";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = ResolveConnectionString();
        await EnsureDatabaseExistsAsync(ConnectionString);

        await using var dbContext = CreateContext();
        await dbContext.Database.MigrateAsync();
        await DbInitializer.SeedProductsAsync(dbContext);
        await ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new AppDbContext(options);
    }

    public OrderService CreateService(AppDbContext dbContext) => new(dbContext);

    public async Task<int> GetProductIdAsync(string productName)
    {
        await using var dbContext = CreateContext();
        var product = await dbContext.Products
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Name == productName);

        return product.Id;
    }

    public async Task<DatabaseState> ReadStateAsync(string productName)
    {
        await using var dbContext = CreateContext();

        var stock = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.Name == productName)
            .Select(product => product.StockQuantity)
            .SingleAsync();

        return new DatabaseState(
            await dbContext.Orders.AsNoTracking().CountAsync(),
            await dbContext.OrderItems.AsNoTracking().CountAsync(),
            await dbContext.Payments.AsNoTracking().CountAsync(),
            stock);
    }

    public async Task ResetAsync()
    {
        await using var dbContext = CreateContext();

        await dbContext.Payments.ExecuteDeleteAsync();
        await dbContext.OrderItems.ExecuteDeleteAsync();
        await dbContext.Orders.ExecuteDeleteAsync();

        var products = await dbContext.Products.ToListAsync();
        foreach (var product in products)
        {
            product.StockQuantity = product.Name switch
            {
                "Laptop" => 10,
                "Keyboard" => 20,
                "Mouse" => 30,
                _ => product.StockQuantity
            };
        }

        await dbContext.SaveChangesAsync();
    }

    private static string ResolveConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'DefaultConnection' was not found. Set it in appsettings.json, appsettings.Local.json, or ConnectionStrings__DefaultConnection.");
        }

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = DatabaseName
        };

        return builder.ConnectionString;
    }

    private static async Task EnsureDatabaseExistsAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            IF DB_ID(N'{DatabaseName}') IS NULL
                CREATE DATABASE [{DatabaseName}];
            """;
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record DatabaseState(
    int OrderCount,
    int OrderItemCount,
    int PaymentCount,
    int StockQuantity);

[CollectionDefinition(SqlServerCollection.Name, DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
