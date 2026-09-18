using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EventTracking.Persistence;

public static class ProductionSecurity
{
    public static bool Required(string environment) => environment is not ("Development" or "Testing");

    public static string Connection(IConfiguration config, bool strict, bool operatorMode)
    {
        string? value = config.GetConnectionString(operatorMode ? "Operator" : "Tracking");
        if (operatorMode && !strict) value ??= config.GetConnectionString("Tracking");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(operatorMode
            ? "Set ConnectionStrings:Operator for database operator commands."
            : "Set ConnectionStrings:Tracking for the runtime database role.");
        NpgsqlConnectionStringBuilder settings;
        try { settings = new(value); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid database connection configuration."); }
        if (operatorMode && ((config["Administration:ExpectedDatabase"] is { Length: > 0 } database && settings.Database != database)
            || (config["Administration:ExpectedHost"] is { Length: > 0 } host && !string.Equals(settings.Host, host, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Operator connection does not match the expected database/host; no migration was attempted.");
        if (strict && (settings.SslMode != SslMode.VerifyFull || settings.GssEncryptionMode != GssEncryptionMode.Disable
            || settings.IncludeErrorDetail || settings.LogParameters || settings.PersistSecurityInfo))
            throw new InvalidOperationException("Production database connections require SSL Mode=VerifyFull;GSS Encryption Mode=Disable, with error details, parameter logging and persisted security info disabled.");
        return value;
    }

    public static void Validate(IConfiguration config, StorageOptions storage, bool strict, bool operatorMode, bool api)
    {
        if (!strict) return;
        if (!storage.Durable) throw new InvalidOperationException("Production requires durable storage.");
        if (!operatorMode && (storage.MigrateOnStartup || storage.AllowProfileTransition))
            throw new InvalidOperationException("Production runtime cannot migrate or change storage profiles; use the operator command.");
        if (!operatorMode && (config.GetConnectionString("Operator") is not null
            || !string.IsNullOrEmpty(config["Dashboard:Bootstrap:Password"])
            || config.GetSection("ProjectAccess:Keys").GetChildren().Any()
            || config.GetSection("Administration").GetChildren().Any()))
            throw new InvalidOperationException("Remove operator, bootstrap and provisioning settings from the runtime environment.");
        if (operatorMode || !api) return;
        if (config.GetValue<bool>("DemoShop:Enabled")) throw new InvalidOperationException("The volatile prototype shop cannot run in Production.");
        string[] hosts = (config["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(h => h.Contains('*') || Uri.CheckHostName(h) == UriHostNameType.Unknown))
            throw new InvalidOperationException("Set AllowedHosts to the explicit public hostnames (semicolon-separated, no wildcards).");
        if (config.GetValue<bool>("ReverseProxy:TrustForwardedProto") && !config.GetValue<bool>("ReverseProxy:ExclusiveIngress"))
            throw new InvalidOperationException("Forwarded protocol requires ReverseProxy:ExclusiveIngress=true and a container reachable only through that trusted proxy.");
    }
}
