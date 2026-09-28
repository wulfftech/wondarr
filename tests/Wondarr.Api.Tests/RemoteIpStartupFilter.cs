using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Wondarr.Api.Tests;

/// <summary>
/// TestServer leaves <c>Connection.RemoteIpAddress</c> null, so tests that exercise the
/// "disabled for local addresses" bypass set it with the <c>X-Test-Remote-Ip</c> header.
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    /// <summary>The request header carrying the address to pretend the request came from.</summary>
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) &&
                IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await nextMiddleware();
        });

        next(app);
    };
}
