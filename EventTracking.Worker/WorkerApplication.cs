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
        var clickHouse = builder.Configuration.GetSection("ClickHouse").Get<ClickHouseOptions>() ?? new();
        clickHouse.Validate();
        bool strict = ProductionSecurity.Required(builder.Environment.EnvironmentName);
        ProductionSecurity.Validate(builder.Configuration, storage, strict, operatorMode: false, api: false);
        if (storage.Profile != "Distributed")
            throw new InvalidOperationException("The worker requires Storage:Profile=Distributed; Hosted and Volatile run without this process.");
        if (storage.MigrateOnStartup || storage.AllowProfileTransition)
            throw new InvalidOperationException("Run migrations and profile transitions through the API operator commands before starting workers.");
        string connection = ProductionSecurity.Connection(builder.Configuration, strict, operatorMode: false);
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton(clickHouse);
        builder.Services.AddHttpClient(nameof(ClickHouseProjector), client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddDbContextFactory<TrackingDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddSingleton<PostgresStore>();
        builder.Services.AddSingleton<ClickHouseProjector>();
        builder.Services.AddHostedService<PostgresWorker>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return builder.Build();
    }
}
