using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Vestrom.Api.Models;

namespace Vestrom.Api.Services;

public sealed class PostgreSqlService(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("PostgreSQL") ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");

    public async Task<PostgresInfo> GetInfoAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            var version = await ScalarAsync<string>(conn, "SHOW server_version", ct) ?? "Unknown";
            var max = await ScalarAsync<int>(conn, "SHOW max_connections", ct);
            var active = await ScalarAsync<int>(conn, "SELECT count(*) FROM pg_stat_activity WHERE state <> 'idle'", ct);
            var databases = await ScalarAsync<int>(conn, "SELECT count(*) FROM pg_database WHERE datallowconn", ct);
            var started = await ScalarAsync<DateTime>(conn, "SELECT pg_postmaster_start_time()", ct);
            return new PostgresInfo("PostgreSQL", "online", version, FormatUptime(DateTime.UtcNow - started.ToUniversalTime()), active, max, databases);
        }
        catch
        {
            return new PostgresInfo("PostgreSQL", "offline", "Unavailable", "Unknown", 0, 0, 0);
        }
    }

    public async Task<DatabaseInfo[]> GetDatabasesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT d.datname, pg_database_size(d.datname), 0::int
            FROM pg_database d WHERE d.datallowconn ORDER BY d.datname;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DatabaseInfo>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            list.Add(new DatabaseInfo(name, name, 0, FormatBytes(reader.GetInt64(1)), "online", DateTimeOffset.UtcNow.ToString("O")));
        }
        foreach (var db in list)
        {
            try
            {
                await using var dbConn = new NpgsqlConnection(DatabaseConnection(db.Id));
                await dbConn.OpenAsync(ct);
                await using var countCmd = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema')", dbConn);
                var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                list[list.IndexOf(db)] = db with { Tables = count };
            }
            catch { }
        }
        return list.ToArray();
    }

    public async Task<DatabaseDetail> GetDatabaseAsync(string databaseId, CancellationToken ct = default)
    {
        var db = await FindDatabaseAsync(databaseId, ct) ?? throw new KeyNotFoundException($"Database '{databaseId}' was not found.");
        var cs = DatabaseConnection(databaseId);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        var schemas = await ReadStringsAsync(conn, "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name", ct);
        var tables = new List<TableInfo>();
        const string tableSql = """
            SELECT n.nspname, c.relname, COALESCE(c.reltuples::bigint,0), pg_total_relation_size(c.oid),
                   (SELECT count(*) FROM information_schema.columns ic WHERE ic.table_schema=n.nspname AND ic.table_name=c.relname)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema') ORDER BY n.nspname,c.relname;
            """;
        await using (var cmd = new NpgsqlCommand(tableSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) tables.Add(new TableInfo(r.GetString(1), r.GetString(0), r.GetInt64(2), FormatBytes(r.GetInt64(3)), r.GetInt32(4)));

        var views = new List<ViewInfo>();
        const string viewSql = "SELECT table_schema, table_name, view_definition FROM information_schema.views WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY table_schema,table_name";
        await using (var cmd = new NpgsqlCommand(viewSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) views.Add(new ViewInfo(r.GetString(1), r.GetString(0), r.IsDBNull(2) ? "" : r.GetString(2)));

        var functions = new List<FunctionInfo>();
        const string functionSql = "SELECT n.nspname, p.proname, pg_get_function_result(p.oid), l.lanname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang WHERE n.nspname NOT IN ('pg_catalog','information_schema') ORDER BY n.nspname,p.proname";
        await using (var cmd = new NpgsqlCommand(functionSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) functions.Add(new FunctionInfo(r.GetString(1), r.GetString(0), r.GetString(2), r.GetString(3)));

        var roles = new List<RoleInfo>();
        const string roleSql = "SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolcanlogin FROM pg_roles ORDER BY rolname";
        await using (var cmd = new NpgsqlCommand(roleSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var attrs = new List<string>();
                if (r.GetBoolean(1)) attrs.Add("SUPERUSER");
                if (r.GetBoolean(2)) attrs.Add("CREATEROLE");
                if (r.GetBoolean(3)) attrs.Add("CREATEDB");
                if (r.GetBoolean(4)) attrs.Add("REPLICATION");
                roles.Add(new RoleInfo(r.GetString(0), attrs.ToArray(), r.GetBoolean(5)));
            }
        return new DatabaseDetail(db, schemas.ToArray(), tables.ToArray(), views.ToArray(), functions.ToArray(), roles.ToArray());
    }

    public async Task<QueryResult> ExecuteQueryAsync(string databaseId, string sql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Enter a SQL statement.");
        if (sql.Length > 20000) throw new ArgumentException("SQL statement is too large (20,000 characters maximum).");
        if (sql.Contains("\0")) throw new ArgumentException("Invalid SQL statement.");
        var trimmed = sql.Trim().TrimEnd(';').Trim();
        if (trimmed.Contains(';')) throw new ArgumentException("Only one SQL statement can be executed at a time.");
        _ = await FindDatabaseAsync(databaseId, ct) ?? throw new KeyNotFoundException($"Database '{databaseId}' was not found.");

        await using var conn = new NpgsqlConnection(DatabaseConnection(databaseId));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(trimmed, conn) { CommandTimeout = 30 };
        var sw = Stopwatch.StartNew();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i))).ToArray();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct) && rows.Count < 500)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : NormalizeValue(reader.GetValue(i));
            rows.Add(row);
        }
        sw.Stop();
        return new QueryResult(columns, rows, rows.Count, sw.ElapsedMilliseconds);
    }

    private async Task<DatabaseInfo?> FindDatabaseAsync(string id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT datname, pg_database_size(datname) FROM pg_database WHERE datname=@name AND datallowconn", conn);
        cmd.Parameters.AddWithValue("name", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DatabaseInfo(r.GetString(0), r.GetString(0), 0, FormatBytes(r.GetInt64(1)), "online", DateTimeOffset.UtcNow.ToString("O"));
    }

    private string DatabaseConnection(string database) => new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct) { await using var cmd = new NpgsqlCommand(sql, conn); var value = await cmd.ExecuteScalarAsync(ct); return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture); }
    private static async Task<List<string>> ReadStringsAsync(NpgsqlConnection conn, string sql, CancellationToken ct) { var list=new List<string>(); await using var cmd=new NpgsqlCommand(sql,conn); await using var r=await cmd.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) list.Add(r.GetString(0)); return list; }
    private static object NormalizeValue(object value) => value switch { DateTime dt => dt.ToString("O"), DateTimeOffset dto => dto.ToString("O"), TimeSpan ts => ts.ToString(), byte[] b => Convert.ToHexString(b), _ => value };
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024*1024 ? $"{bytes/1024d:0.##} KB" : bytes < 1024*1024*1024 ? $"{bytes/1024d/1024:0.##} MB" : $"{bytes/1024d/1024/1024:0.##} GB";
    private static string FormatUptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";
}
