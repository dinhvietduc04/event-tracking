using Npgsql;

namespace EventTracking.Persistence;

public static class RuntimeDatabaseAccess
{
    // No defaults for future tables: review grants each time a schema is added.
    private static readonly Dictionary<string, string[]> Api = new()
    {
        ["__EFMigrationsHistory"] = ["SELECT"],
        ["storage_state"] = ["SELECT", "UPDATE (id)"],
        ["projects"] = ["SELECT", "INSERT", "UPDATE"],
        ["credentials"] = ["SELECT", "INSERT", "UPDATE"],
        ["event_identity"] = ["SELECT", "INSERT"],
        ["events"] = ["SELECT", "INSERT"],
        ["inbox"] = ["SELECT", "INSERT"],
        ["dashboard_users"] = ["SELECT", "UPDATE (id)"],
        ["project_memberships"] = ["SELECT", "INSERT", "UPDATE", "DELETE"],
        ["audit_records"] = ["SELECT", "INSERT"],
        ["data_protection_keys"] = ["SELECT", "INSERT"],
        ["login_rate_limits"] = ["SELECT", "INSERT", "UPDATE"],
        // Product analytics definitions are dashboard-owned; runtime may read/write them but no other table is broadened.
        ["saved_query_views"] = ["SELECT", "INSERT", "UPDATE"],
        ["event_schemas"] = ["SELECT", "INSERT"],
    };
    private static readonly Dictionary<string, string[]> Worker = new()
    {
        ["__EFMigrationsHistory"] = ["SELECT"],
        ["storage_state"] = ["SELECT", "UPDATE (id)"],
        ["inbox"] = ["SELECT", "UPDATE (processed_at)"],
        ["events"] = ["SELECT", "INSERT"],
    };

    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);

    private static Dictionary<string, string[]> Grants(string kind) =>
        kind switch
        {
            "Api" => Api,
            "Worker" => Worker,
            _ => throw new InvalidOperationException("Use Administration:RoleKind=Api or Worker."),
        };

    public static async Task Grant(
        NpgsqlDataSource source,
        string role,
        string kind,
        CancellationToken ct = default
    )
    {
        var grants = Grants(kind);
        if (string.IsNullOrWhiteSpace(role) || role.Length > 63 || role.Any(char.IsControl))
            throw new InvalidOperationException(
                "Supply an existing runtime login in Administration:RoleName."
            );
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await CheckRole(connection, transaction, role, ct);
        string database;
        await using (
            var name = DatabaseSql.Command(connection, transaction, "SELECT current_database()")
        )
            database = (string)(await name.ExecuteScalarAsync(ct))!;
        // This command is for a dedicated application database. PUBLIC must not bypass the grants.
        await Execute(
            $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE TEMPORARY ON DATABASE {Quote(database)} FROM PUBLIC; REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC; REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC"
        );
        await Execute(
            $"REVOKE ALL ON DATABASE {Quote(database)} FROM {Quote(role)}; REVOKE ALL ON SCHEMA public FROM {Quote(role)}; REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {Quote(role)}; REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {Quote(role)}"
        );
        // Table-level REVOKE does not clear old column grants.
        List<(string Table, string Column)> columns = [];
        await using (
            var query = DatabaseSql.Command(
                connection,
                transaction,
                "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public'"
            )
        )
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                columns.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var (table, column) in columns)
            await Execute(
                $"REVOKE ALL ({Quote(column)}) ON TABLE public.{Quote(table)} FROM {Quote(role)}"
            );
        await Execute(
            $"GRANT CONNECT ON DATABASE {Quote(database)} TO {Quote(role)}; GRANT USAGE ON SCHEMA public TO {Quote(role)}"
        );
        foreach (var grant in grants)
            await Execute(
                $"GRANT {string.Join(',', grant.Value)} ON TABLE public.{Quote(grant.Key)} TO {Quote(role)}"
            );
        if (kind == "Api")
            await Execute(
                $"GRANT USAGE,SELECT ON SEQUENCE public.data_protection_keys_id_seq TO {Quote(role)}"
            );
        await AuditLog.Write(
            connection,
            transaction,
            "operator:database-grants",
            null,
            "database.runtime-granted",
            role,
            new { kind },
            ct
        );
        await transaction.CommitAsync(ct);

        async Task Execute(string sql)
        {
            await using var command = DatabaseSql.Command(connection, transaction, sql);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task Verify(
        NpgsqlDataSource source,
        string kind,
        CancellationToken ct = default
    )
    {
        var grants = Grants(kind);
        await using var connection = await source.OpenConnectionAsync(ct);
        string role;
        await using (
            var current = DatabaseSql.Command(
                connection,
                null,
                "SELECT current_user WHERE current_user=session_user"
            )
        )
            role =
                await current.ExecuteScalarAsync(ct) as string
                ?? throw new InvalidOperationException(
                    "Runtime must authenticate directly as its service role, without SET ROLE."
                );
        if (role != new NpgsqlConnectionStringBuilder(source.ConnectionString).Username)
            throw new InvalidOperationException(
                "Runtime database login must match its effective role; do not disguise an operator connection with session options."
            );
        await CheckRole(connection, null, role, ct);
        await using (
            var broad = DatabaseSql.Command(
                connection,
                null,
                "SELECT has_database_privilege(current_database(),'CREATE') OR has_database_privilege(current_database(),'TEMP') OR has_schema_privilege('public','CREATE')"
            )
        )
            if ((bool)(await broad.ExecuteScalarAsync(ct))!)
                throw new InvalidOperationException(
                    "Runtime database role has database/schema creation privileges. Apply reviewed runtime grants."
                );
        // Inspect every column too: table-level checks alone miss column grants.
        await using var query = DatabaseSql.Command(
            connection,
            null,
            """
            SELECT c.relname,a.attname,p.privilege,has_column_privilege(c.oid,a.attnum,p.privilege),
                has_column_privilege(c.oid,a.attnum,p.privilege || ' WITH GRANT OPTION')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            CROSS JOIN (VALUES ('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            UNION ALL
            SELECT c.relname,'',p.privilege,has_table_privilege(c.oid,p.privilege),
                has_table_privilege(c.oid,p.privilege || ' WITH GRANT OPTION')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN (VALUES ('DELETE'),('TRUNCATE'),('TRIGGER')) p(privilege)
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            """
        );
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                string table = reader.GetString(0),
                    column = reader.GetString(1),
                    privilege = reader.GetString(2);
                bool allowed =
                    grants.TryGetValue(table, out var required)
                    && (
                        required.Contains(privilege) || required.Contains($"{privilege} ({column})")
                    );
                if (reader.GetBoolean(3) != allowed || reader.GetBoolean(4))
                    throw new InvalidOperationException(
                        "Runtime database grants do not match the selected service. Reapply --grant-runtime after migration."
                    );
            }
        await using var sequences = DatabaseSql.Command(
            connection,
            null,
            """
            SELECT c.relname,p.privilege,has_sequence_privilege(c.oid,p.privilege),
                has_sequence_privilege(c.oid,p.privilege || ' WITH GRANT OPTION')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN (VALUES ('USAGE'),('SELECT'),('UPDATE')) p(privilege)
            WHERE n.nspname='public' AND c.relkind='S'
            """
        );
        await using var sequenceReader = await sequences.ExecuteReaderAsync(ct);
        while (await sequenceReader.ReadAsync(ct))
        {
            bool allowed =
                kind == "Api"
                && sequenceReader.GetString(0) == "data_protection_keys_id_seq"
                && sequenceReader.GetString(1) is "USAGE" or "SELECT";
            if (sequenceReader.GetBoolean(2) != allowed || sequenceReader.GetBoolean(3))
                throw new InvalidOperationException(
                    "Runtime sequence grants do not match the selected service. Reapply --grant-runtime."
                );
        }
    }

    private static async Task CheckRole(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string role,
        CancellationToken ct
    )
    {
        await using var query = DatabaseSql.Command(
            connection,
            transaction,
            """
            SELECT NOT rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
                OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid)
                OR EXISTS(SELECT 1 FROM pg_database WHERE datname=current_database() AND datdba=r.oid)
                OR EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='public' AND nspowner=r.oid)
                OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relowner=r.oid)
            FROM pg_roles r WHERE rolname=$1
            """,
            role
        );
        if (await query.ExecuteScalarAsync(ct) is not false)
            throw new InvalidOperationException(
                "Use a dedicated LOGIN role without ownership, role memberships, superuser, CREATEDB, CREATEROLE, replication or BYPASSRLS privileges."
            );
    }
}
