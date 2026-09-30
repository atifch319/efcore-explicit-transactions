# EF Core Explicit Transactions in ASP.NET Core

This project demonstrates the difference between multiple `SaveChanges()` calls and an explicit EF Core database transaction.

The workflow is a realistic order:

```text
Create Order → Update Inventory → Create Payment
```

`SaveChanges()` persists the changes included in that call transactionally. It does not automatically join several later `SaveChanges()` calls into one business transaction. When every step must succeed or fail together, the workflow needs an explicit database transaction.

## What This Project Demonstrates

- EF Core `SaveChanges()`
- Multiple independent database operations
- Explicit database transactions
- Commit
- Rollback
- Partial persistence
- All-or-nothing workflows
- SQL Server
- Integration testing

## Important Concept

`SaveChanges()` itself uses a transaction for the changes being persisted by that call. SQL Server commits that unit of work when the call completes.

A business workflow can still contain several independent database operations. If the code calls `SaveChanges()` more than once, each call is its own transaction. Those calls are not automatically one single business transaction spanning the entire workflow.

```text
SaveChanges()
    ↓
Order committed

SaveChanges()
    ↓
Inventory committed

Payment fails
```

The order and the inventory update can remain in the database. The payment never arrives. The data is only partially consistent.

An explicit transaction changes the boundary:

```text
BeginTransaction()
    ↓
Order
    ↓
Inventory
    ↓
Payment
    ↓
Commit()
```

`SaveChanges()` still runs inside that transaction. It enlists in the transaction that is already open. It does not commit that outer transaction. EF Core creates a savepoint around each `SaveChanges()` call so a failed save can return to that savepoint. The business transaction still ends only when the code calls `Commit()` or `Rollback()`. `Commit()` makes every step durable. If a step fails:

```text
Failure
    ↓
Rollback()
```

SQL Server reverts the order, the order items, and the inventory update that were saved earlier in the same transaction.

A single `SaveChanges()` that already contains every related change does not need a second, hand-written transaction. The explicit transaction matters when the workflow itself is split across multiple commits.

## When Should You Use an Explicit Transaction?

Use an explicit transaction when:

- Multiple `SaveChanges()` calls must behave as one unit
- Multiple tables must remain consistent
- A business operation requires all-or-nothing behavior
- Multiple database operations must commit together

You do not need an explicit transaction around every `SaveChanges()` call. One call already commits its own tracked changes atomically. Add a transaction when the business operation is larger than one call.

This project keeps retry strategies off. Enabling SQL Server retry with `EnableRetryOnFailure` requires the execution strategy to create the transaction, because a retry must start from the beginning of the workflow.

## Project Structure

```text
efcore-explicit-transactions/
├── src/EfCoreTransactions.Api/
│   ├── Controllers/OrdersController.cs
│   ├── Data/
│   ├── DTOs/
│   ├── Models/
│   ├── Services/OrderService.cs
│   └── Transactions/TransactionMode.cs
├── tests/EfCoreTransactions.Tests/
├── EfCoreTransactions.sln
└── Directory.Build.props
```

`OrderService` has two paths:

- `WithoutExplicitTransaction` calls `SaveChangesAsync` after the order, after the inventory update, and after the payment.
- `WithExplicitTransaction` runs those same calls inside `BeginTransactionAsync`, then `CommitAsync`. A failure calls `RollbackAsync`.

`TransactionMode` selects the path. The API maps `useExplicitTransaction` onto that enum.

## API Examples

`POST /api/orders`

Successful order inside an explicit transaction:

```http
POST /api/orders
Content-Type: application/json
```

```json
{
  "productId": 1,
  "quantity": 2,
  "useExplicitTransaction": true,
  "simulatePaymentFailure": false
}
```

A successful call returns `201 Created` and the saved order. On a fresh database the seeded products are Laptop (`1`), Keyboard (`2`), and Mouse (`3`).

Simulated payment failure:

```json
{
  "productId": 1,
  "quantity": 1,
  "useExplicitTransaction": false,
  "simulatePaymentFailure": true
}
```

The service throws `InvalidOperationException` before the payment is saved. The API logs the exception and returns `500` with a problem response. The response does not include the exception message or a stack trace.

| `useExplicitTransaction` | Result after the simulated payment failure |
| ------------------------ | ------------------------------------------ |
| `false`                  | The order and the inventory update remain. The payment does not. |
| `true`                   | The transaction rolls back. No order, order item, or payment remains, and stock is unchanged. |

Other responses:

- `400` when the request fails validation
- `404` when the product does not exist
- `409` when stock is insufficient

Insufficient stock is rejected before any write, in both transaction modes.

## Test Scenarios

| Scenario | Explicit Transaction | Failure | Expected Result            |
| -------- | -------------------- | ------- | -------------------------- |
| Success  | No                   | No      | All changes persist        |
| Failure  | No                   | Yes     | Partial changes may remain |
| Success  | Yes                  | No      | All changes persist        |
| Failure  | Yes                  | Yes     | All changes roll back      |

The automated tests also check that payment failure throws `InvalidOperationException` with the message `Simulated payment failure.`, and that requesting more stock than the product has leaves the database unchanged.

Tests use the SQL Server provider against database `EfCoreTransactionsTestsDb`. They do not use the EF Core InMemory provider. InMemory does not implement SQL Server transaction behavior, so it cannot demonstrate commit and rollback.

Each test resets orders, order items, payments, and product stock before it runs. Assertions read through a new `DbContext`, so they observe committed database state rather than the change tracker of the failed request. `WebApplicationFactory` covers `POST /api/orders`, including the problem response and the database state behind it.

## Run the Project

Prerequisites:

- .NET 10 SDK
- SQL Server reachable with the connection string below

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project .\src\EfCoreTransactions.Api\
```

The `http` launch profile listens on `http://localhost:5166`. Development startup applies migrations and seeds products when the database has none. The console logs EF Core transaction and command messages.

OpenAPI is available at `http://localhost:5166/openapi/v1.json` while the app is running in Development.

## EF Core Migration

The initial migration is already in `src/EfCoreTransactions.Api/Data/Migrations`. Apply it with:

```powershell
dotnet tool restore
dotnet ef database update --project .\src\EfCoreTransactions.Api\
```

`dotnet tool restore` installs the local `dotnet-ef` tool from `.config/dotnet-tools.json`.

That migration was created with:

```powershell
dotnet ef migrations add InitialCreate --project .\src\EfCoreTransactions.Api\ --output-dir Data/Migrations
```

Do not run `migrations add InitialCreate` again. The migration is already in the repository. For a later model change, choose a new migration name:

```powershell
dotnet ef migrations add <MigrationName> --project .\src\EfCoreTransactions.Api\ --output-dir Data/Migrations
dotnet ef database update --project .\src\EfCoreTransactions.Api\
```

Development also calls `Database.MigrateAsync()` at startup so the demo database is created and seeded. Other environments do not apply migrations automatically.

## SQL Server Requirement

The committed connection string uses Windows authentication and does not contain a password:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=EfCoreTransactionsDb;Trusted_Connection=True;TrustServerCertificate=True;"
}
```

Change the server name if SQL Server is not on `localhost`. Keep credentials out of the repository.

For SQL authentication, store the connection string in user secrets:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=YOUR_SERVER;Database=EfCoreTransactionsDb;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;" --project .\src\EfCoreTransactions.Api\
```

`dotnet ef` reads the same configuration through `AppDbContextFactory`, including user secrets, `appsettings.Local.json`, and environment variables. `appsettings.Local.json` is gitignored.

Integration tests force the catalog to `EfCoreTransactionsTestsDb`, so they do not write into the application database. Override the test connection with `tests/EfCoreTransactions.Tests/appsettings.Local.json` or with the `ConnectionStrings__DefaultConnection` environment variable. The test process still replaces the database name with `EfCoreTransactionsTestsDb`.

The Windows account running the tests needs permission to create that database and to apply migrations.

## Testing

Run:

```powershell
dotnet test
```

These are SQL Server integration tests. The command output is the test result. A passing run reports every test as `Passed` and prints the total count. This repository does not use the InMemory provider to stand in for that result.

Seeded products:

| Name     | Stock | Price |
| -------- | ----- | ----- |
| Laptop   | 10    | 1500  |
| Keyboard | 20    | 75    |
| Mouse    | 30    | 25    |

## License

MIT License. See [LICENSE](LICENSE).
