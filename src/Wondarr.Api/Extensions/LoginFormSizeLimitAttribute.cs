using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Wondarr.Api.Extensions;

/// <summary>
/// Caps the body of a login form before it is read. Kestrel enforces the cap through
/// <see cref="IHttpMaxRequestBodySizeFeature"/>, which also covers a chunked body; a declared
/// <c>Content-Length</c> over the cap is refused with 413 here, without reading a byte, on any
/// server (the in-memory test server has no such feature, so <c>[RequestSizeLimit]</c> alone does nothing there).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LoginFormSizeLimitAttribute(int maxBytes) : Attribute, IResourceFilter
{
    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }

        if (request.ContentLength > maxBytes)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
        }
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
