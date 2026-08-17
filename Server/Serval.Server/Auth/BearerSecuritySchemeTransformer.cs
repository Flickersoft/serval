using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Serval.Server.Auth;

/// <summary>
/// Adds a Bearer security scheme to the generated OpenAPI document so Scalar's UI
/// (<c>/scalar/v1</c>) shows an "Authorize" button: log in via <c>POST /api/auth/login</c>, paste
/// the access token, and every call made from Scalar from then on carries it. The API's shape
/// stays public either way (this project is open source and Serval:OpenApi:Enabled is left as the
/// only gate on that, deliberately — see ServerOptions.OpenApiOptions) — this only lets Scalar
/// actually *exercise* the protected routes it documents.
/// </summary>
public sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemes)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (!(await schemes.GetAllSchemesAsync()).Any(s => s.Name == JwtBearerDefaults.AuthenticationScheme))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access token from POST /api/auth/login.",
        };

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        };

        foreach (OpenApiOperation operation in document.Paths.Values.SelectMany(p => p.Operations!.Values))
        {
            operation.Security ??= [];
            operation.Security.Add(requirement);
        }
    }
}
