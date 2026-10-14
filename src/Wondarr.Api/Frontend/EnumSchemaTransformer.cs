using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Wondarr.Api.Frontend;

/// <summary>
/// Types enums in the OpenAPI document the way the API sends them. The controllers' JSON options
/// add a <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>, which the OpenAPI
/// generator does not see, so it would describe every enum as an integer and the generated
/// frontend types would be wrong. The names are produced by serialising each member with the
/// configured options, so the naming policy is never re-implemented here; an enum those options
/// write as a number keeps its integer schema.
/// </summary>
internal sealed class EnumSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        var type = context.JsonTypeInfo.Type;
        var nullable = Nullable.GetUnderlyingType(type);
        var enumType = nullable ?? type;

        if (!enumType.IsEnum)
        {
            return Task.CompletedTask;
        }

        var options = context.ApplicationServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;

        var names = new List<JsonNode>();

        foreach (var value in Enum.GetValues(enumType))
        {
            var written = JsonSerializer.SerializeToNode(value, enumType, options);

            if (written is not JsonValue json || !json.TryGetValue<string>(out var name))
            {
                // Written as a number: the generator's integer schema is already right.
                return Task.CompletedTask;
            }

            names.Add(JsonValue.Create(name));
        }

        var isNullable = nullable is not null || (schema.Type is { } existing && existing.HasFlag(JsonSchemaType.Null));

        schema.Type = isNullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.Format = null;
        schema.Enum = names;

        if (isNullable)
        {
            schema.Enum.Add(null!);
        }

        return Task.CompletedTask;
    }
}
