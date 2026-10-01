namespace EventTracking.Api.Auth;

public sealed record ProjectPermission(string Name);

public sealed record ProjectContext(string ProjectId, string[] Permissions);

public sealed class DevelopmentKey
{
    public string ProjectId { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string[] Permissions { get; set; } = [];
    public bool Revoked { get; set; }

    public static void Validate(DevelopmentKey key)
    {
        if (
            string.IsNullOrWhiteSpace(key.ProjectId)
            || key.ProjectId.Length > 100
            || key.KeyHash is null
            || key.KeyHash.Length != 64
            || !key.KeyHash.All(Uri.IsHexDigit)
            || key.Permissions is null
            || key.Permissions.Length == 0
            || key.Permissions.Any(p => p is not ("ingest" or "read"))
        )
            throw new InvalidOperationException("Invalid project/key configuration.");
    }
}

// Persistent credential provisioning lives in DatabaseSetup; runtime auth is PostgresKeys only.
public interface IProjectKeys
{
    ValueTask<ProjectContext?> AuthenticateAsync(string token, CancellationToken ct);
}

public static class ProjectAccess
{
    public static ProjectContext Project(this HttpContext context) =>
        (ProjectContext)context.Items[typeof(ProjectContext)]!;

    public static async Task Authorize(HttpContext context, RequestDelegate next)
    {
        var permission = context.GetEndpoint()?.Metadata.GetMetadata<ProjectPermission>();
        if (permission is null)
        {
            await next(context);
            return;
        }
        string authorization = context.Request.Headers.Authorization.ToString();
        var project = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? await context
                .RequestServices.GetRequiredService<IProjectKeys>()
                .AuthenticateAsync(authorization[7..], context.RequestAborted)
            : null;
        if (project is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Results
                .Problem(statusCode: 401, title: "A valid project API key is required.")
                .ExecuteAsync(context);
            return;
        }
        if (!project.Permissions.Contains(permission.Name))
        {
            await Results
                .Problem(statusCode: 403, title: $"The key requires {permission.Name} permission.")
                .ExecuteAsync(context);
            return;
        }
        context.Items[typeof(ProjectContext)] = project;
        context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }
}
