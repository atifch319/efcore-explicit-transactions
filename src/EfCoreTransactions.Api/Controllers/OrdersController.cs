using EfCoreTransactions.Api.DTOs;
using EfCoreTransactions.Api.Services;
using EfCoreTransactions.Api.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace EfCoreTransactions.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderService orderService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<OrderResponse>> CreateOrder(
        [FromBody] CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var transactionMode = request.UseExplicitTransaction
            ? TransactionMode.WithExplicitTransaction
            : TransactionMode.WithoutExplicitTransaction;

        var response = await orderService.CreateOrderAsync(
            request,
            transactionMode,
            request.SimulatePaymentFailure,
            cancellationToken);

        return Created($"/api/orders/{response.OrderId}", response);
    }
}
