using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Compilarr.Api.Frontend;

/// <summary>
/// Makes the OpenAPI document independent of the host it was generated on: the <c>servers</c>
/// array is dropped (clients use the URL base they were given) and the info block is fixed, so the
/// snapshot committed to <c>docs/api/openapi.json</c> only changes when the API does.
/// </summary>
internal sealed class CompilarrDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "Compilarr",
            Version = "v1",
        };

        document.Servers?.Clear();

        return Task.CompletedTask;
    }
}
