using EfCoreTransactions.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EfCoreTransactions.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        await SeedProductsAsync(dbContext, cancellationToken);
    }

    public static async Task SeedProductsAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        dbContext.Products.AddRange(
            new Product { Name = "Laptop", StockQuantity = 10, Price = 1500m },
            new Product { Name = "Keyboard", StockQuantity = 20, Price = 75m },
            new Product { Name = "Mouse", StockQuantity = 30, Price = 25m });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
