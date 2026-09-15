using System.Security.Cryptography;
using System.Text;
using EventTracking.Api.Persistence;
using Npgsql;

namespace EventTracking.Api.Access;

public sealed class PostgresKeys(NpgsqlDataSource source) : IProjectKeys
{
    public async ValueTask<ProjectContext?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 32 or > 256) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT project_id,permissions FROM credentials WHERE key_hash=$1 AND NOT revoked", hash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetFieldValue<string[]>(1)) : null;
    }
}
