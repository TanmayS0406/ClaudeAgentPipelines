using System.Net;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Security;

/// <summary>Rejects requests that do not come from this computer, unless Security:AllowRemoteClients is set.</summary>
public sealed class LocalClientsOnlyMiddleware(RequestDelegate next, IOptionsMonitor<SecurityOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (options.CurrentValue.AllowRemoteClients || IsLocal(context.Connection.RemoteIpAddress))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync(
            "Claude Agent Pipelines only accepts requests from this computer, because pipelines run on its Claude account.");
    }

    private static bool IsLocal(IPAddress? address) => address is null || IPAddress.IsLoopback(address);
}
