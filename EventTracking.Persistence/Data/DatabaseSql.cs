using Npgsql;

namespace EventTracking.Persistence;

/// <summary>
/// Thin Npgsql command factory. Centralises positional ($1, $2, …) parameter
/// binding so store code stays focused on SQL + transaction boundaries.
/// </summary>
public static class DatabaseSql
{
    public static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params object?[] values
    )
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }
}
