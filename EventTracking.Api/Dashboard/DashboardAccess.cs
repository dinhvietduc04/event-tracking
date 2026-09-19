using System.Security.Claims;
using EventTracking.Api.Access;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public sealed record DashboardPermission(
    bool Project = false,
    bool Demo = false,
    bool Manage = false
);

public static class DashboardAccess
{
    public const string Scheme = "Dashboard";

    public static Guid UserId(this HttpContext context) =>
        Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static void Configure(IServiceCollection services, bool development)
    {
        services
            .AddAuthentication(Scheme)
            .AddCookie(
                Scheme,
                options =>
                {
                    options.Cookie.Name = "event-tracking.dashboard";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = development
                        ? CookieSecurePolicy.SameAsRequest
                        : CookieSecurePolicy.Always;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = false;
                    options.Events = new CookieAuthenticationEvents
                    {
                        OnRedirectToLogin = context =>
                        {
                            context.Response.StatusCode = 401;
                            return Task.CompletedTask;
                        },
                        OnRedirectToAccessDenied = context =>
                        {
                            context.Response.StatusCode = 403;
                            return Task.CompletedTask;
                        },
                    };
                }
            );
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = "event-tracking.csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = development
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });
    }

    public static async Task Authorize(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/dashboard-api"))
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        var permission = context.GetEndpoint()?.Metadata.GetMetadata<DashboardPermission>();
        if (permission is not null)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await Denied(context, 401, "Sign in to continue.");
                return;
            }
            await using var connection = await context
                .RequestServices.GetRequiredService<NpgsqlDataSource>()
                .OpenConnectionAsync(context.RequestAborted);
            if (!int.TryParse(context.User.FindFirstValue("session_version"), out int version))
            {
                await context.SignOutAsync(Scheme);
                await Denied(context, 401, "Sign in again to continue.");
                return;
            }
            await using var user = DatabaseSql.Command(
                connection,
                null,
                "SELECT count(*) FROM dashboard_users WHERE id=$1 AND NOT disabled AND session_version=$2",
                context.UserId(),
                version
            );
            if (Convert.ToInt64(await user.ExecuteScalarAsync(context.RequestAborted)) != 1)
            {
                await context.SignOutAsync(Scheme);
                await Denied(context, 401, "Your account is no longer active.");
                return;
            }
            if (permission.Project)
            {
                var projectId = context.Request.RouteValues["projectId"]?.ToString() ?? "";
                await using var member = DatabaseSql.Command(
                    connection,
                    null,
                    "SELECT can_demo,can_manage FROM project_memberships WHERE user_id=$1 AND project_id=$2",
                    context.UserId(),
                    projectId
                );
                await using var reader = await member.ExecuteReaderAsync(context.RequestAborted);
                if (!await reader.ReadAsync(context.RequestAborted))
                {
                    await Denied(context, 403, "You do not have access to this project or action.");
                    return;
                }
                bool allowed = reader.GetBoolean(0),
                    manage = reader.GetBoolean(1);
                if ((permission.Demo && !allowed) || (permission.Manage && !manage))
                {
                    await Denied(context, 403, "You do not have access to this project or action.");
                    return;
                }
                context.Items[typeof(ProjectContext)] = new ProjectContext(
                    projectId,
                    allowed ? ["read", "ingest"] : ["read"]
                );
            }
        }
        if (
            HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
        )
        {
            try
            {
                await context
                    .RequestServices.GetRequiredService<IAntiforgery>()
                    .ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Denied(context, 400, "Refresh this page before submitting again.");
                return;
            }
        }
        await next(context);
    }

    private static Task Denied(HttpContext context, int code, string title) =>
        Results.Problem(statusCode: code, title: title).ExecuteAsync(context);
}
