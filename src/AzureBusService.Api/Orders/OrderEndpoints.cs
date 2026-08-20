using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AzureBusService.Api.Telemetry;
using AzureBusService.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace AzureBusService.Api.Orders;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app, bool authenticationEnabled, long maxRequestBodySize)
    {
        var group = app.MapGroup("/api/v1/orders");
        if (authenticationEnabled)
        {
            group.RequireAuthorization("orders");
        }

        group.MapPost(string.Empty, CreateOrderAsync)
            .WithName("CreateOrder")
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodySize));
        group.MapGet("/{id:guid}", GetOrderAsync)
            .WithName("GetOrder");
    }

    private static async Task<IResult> CreateOrderAsync(
        HttpContext httpContext,
        CreateOrderRequest? request,
        OrderService orderService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdempotencyKey(httpContext.Request.Headers, out var idempotencyKey, out var keyError))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = [keyError]
            });
        }

        var validation = OrderRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.Errors);
        }

        var owner = OrderOwner.Get(httpContext.User);
        var result = await orderService.CreateAsync(
            owner,
            idempotencyKey!,
            validation.Value!,
            cancellationToken);
        if (result.Outcome == OrderCreationOutcome.Conflict)
        {
            ApiMetrics.OrderConflicts.Add(1);
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency key conflict",
                detail: "The idempotency key was already used with a different request payload.");
        }

        var location = $"/api/v1/orders/{result.OrderId:D}";
        httpContext.Response.Headers.RetryAfter = "1";
        ApiMetrics.OrdersAccepted.Add(1);
        return Results.Accepted(location, new AcceptedOrderResponse(
            result.OrderId,
            OrderStatus.Pending.ToString(),
            location,
            result.CreatedUtc,
            1));
    }

    private static async Task<IResult> GetOrderAsync(
        Guid id,
        HttpContext httpContext,
        IDbContextFactory<OrdersDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        var owner = OrderOwner.Get(httpContext.User);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var order = await dbContext.Orders.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OwnerSubject == owner, cancellationToken);
        if (order is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found");
        }

        var etag = OrderHttpCache.CreateEtag(order.RowVersion);
        httpContext.Response.Headers.ETag = etag;
        if (OrderHttpCache.Matches(httpContext.Request.Headers.IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (order.Status is OrderStatus.Pending or OrderStatus.Queued or OrderStatus.Processing)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        var failure = order.Status == OrderStatus.Failed
            ? OrderFailureResponse.FromStored(order.FailureCode, order.FailureMessage)
            : null;
        return Results.Ok(new OrderStatusResponse(
            order.Id,
            order.CustomerId,
            order.Currency,
            order.Total,
            order.Status.ToString(),
            order.CreatedUtc,
            order.UpdatedUtc,
            failure));
    }

    private static bool TryGetIdempotencyKey(
        IHeaderDictionary headers,
        out string? idempotencyKey,
        out string error)
    {
        StringValues values = headers["Idempotency-Key"];
        if (values.Count != 1)
        {
            idempotencyKey = null;
            error = "Exactly one Idempotency-Key header is required.";
            return false;
        }

        return IdempotencyKey.TryNormalize(values[0], out idempotencyKey, out error);
    }
}

public static class OrderOwner
{
    public static string Get(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        return TryGetAuthenticated(principal, out var owner)
            ? owner!
            : throw new InvalidOperationException("Authenticated principal is missing valid issuer or subject claims.");
    }

    public static bool TryGetAuthenticated(ClaimsPrincipal principal, out string? owner)
    {
        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 2048 ||
            string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            owner = null;
            return false;
        }

        owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\u001f{subject}")));
        return true;
    }
}

public static class OrderHttpCache
{
    public static string CreateEtag(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    public static bool Matches(StringValues ifNoneMatch, string currentEtag)
    {
        foreach (var value in ifNoneMatch.SelectMany(value => value?.Split(',') ?? []))
        {
            var candidate = value.Trim();
            if (candidate == "*" || string.Equals(candidate, currentEtag, StringComparison.Ordinal) ||
                string.Equals(candidate, $"W/{currentEtag}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
