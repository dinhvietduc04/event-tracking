using EventTracking.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTracking.Worker;

public static class WorkerApplication
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
        storage.Validate(builder.Configuration);
        if (storage.Profile != "Distributed")
            throw new InvalidOperationException("The worker requires Storage:Profile=Distributed; Hosted and Volatile run without this process.");
        if (storage.MigrateOnStartup || storage.AllowProfileTransition)
            throw new InvalidOperationException("Run migrations and profile transitions through the API operator commands before starting workers.");
        string connection = builder.Configuration.GetConnectionString("Tracking")
            ?? throw new InvalidOperationException("Set ConnectionStrings:Tracking for the worker.");
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddDbContextFactory<TrackingDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddSingleton<PostgresStore>();
        builder.Services.AddHostedService<PostgresWorker>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return builder.Build();
    }
}
