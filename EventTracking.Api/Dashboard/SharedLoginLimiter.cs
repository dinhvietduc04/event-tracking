using System.Security.Cryptography;
using System.Text;
using EventTracking.Persistence;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public sealed class SharedLoginLimiter(NpgsqlDataSource source)
{
    // Fixed 4096 account buckets + one global bucket bound storage even for random usernames.
    // Collisions conservatively share a limit; no username or untrusted forwarded IP is stored.
    public async Task<bool> Acquire(string? username, CancellationToken ct)
    {
        string normalized = username is { Length: <= 60 }
            ? username.Trim().ToLowerInvariant()
            : "invalid";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string bucket = "account-" + ((hash[0] << 4) | (hash[1] >> 4)).ToString("x3");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        bool allowed = await Increment("global", 120) && await Increment(bucket, 10);
        await transaction.CommitAsync(ct); // Denied attempts also consume the global budget.
        return allowed;

        async Task<bool> Increment(string key, int limit)
        {
            await using var command = DatabaseSql.Command(
                connection,
                transaction,
                """
                INSERT INTO login_rate_limits(bucket,window_start,attempts) VALUES($1,statement_timestamp(),1)
                ON CONFLICT(bucket) DO UPDATE SET
                    attempts=CASE WHEN login_rate_limits.window_start>EXCLUDED.window_start-interval '1 minute' THEN least(login_rate_limits.attempts+1,$2+1) ELSE 1 END,
                    window_start=CASE WHEN login_rate_limits.window_start>EXCLUDED.window_start-interval '1 minute' THEN login_rate_limits.window_start ELSE EXCLUDED.window_start END
                RETURNING attempts
                """,
                key,
                limit
            );
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) <= limit;
        }
    }
}
