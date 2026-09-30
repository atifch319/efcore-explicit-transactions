namespace EfCoreTransactions.Api.Services;

public sealed class InsufficientInventoryException : Exception
{
    public InsufficientInventoryException(string productName, int available, int requested)
        : base($"Insufficient stock for '{productName}'. Available: {available}. Requested: {requested}.")
    {
        ProductName = productName;
        Available = available;
        Requested = requested;
    }

    public string ProductName { get; }

    public int Available { get; }

    public int Requested { get; }
}
