using Microsoft.AspNetCore.Diagnostics;

namespace Serval.Server;

/// <summary>
/// A request refused for what it says rather than for how the server failed, carrying a message
/// meant for the person who submitted it. Thrown from anywhere under a request and turned into a
/// 400 by <see cref="ValidationExceptionHandler"/>, so no endpoint needs its own catch.
/// </summary>
public abstract class ValidationException(string message) : Exception(message);

/// <summary>Maps any <see cref="ValidationException"/> to a 400 with the message as the body,
/// in the <c>{ "error": ... }</c> shape every refusal on this API takes.</summary>
public sealed class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message }, cancellationToken);
        return true;
    }
}
