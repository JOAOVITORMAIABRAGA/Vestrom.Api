using Npgsql;
using Vestrom.Api.Services;

namespace Vestrom.Api.Services;

public static class AdminSetup
{
    public static async Task RunAsync(IConfiguration configuration, AuthService auth)
    {
        var cs = configuration.GetConnectionString("PostgreSQL") ?? throw new InvalidOperationException("ConnectionStrings:PostgreSQL is required.");
        await auth.EnsureSchemaAsync();
        Console.Write("Admin username: "); var username = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Username is required.");
        Console.Write("Display name: "); var display = Console.ReadLine()?.Trim(); if (string.IsNullOrWhiteSpace(display)) display = username;
        Console.Write("Password: "); var password = ReadPassword(); Console.WriteLine();
        if (password.Length < 10) throw new InvalidOperationException("Password must have at least 10 characters.");
        var hash = auth.HashPassword(username, password);
        await using var conn = new NpgsqlConnection(cs); await conn.OpenAsync();
        const string sql = "INSERT INTO vestrom_users(username,display_name,password_hash,role) VALUES(@u,@d,@h,'admin') ON CONFLICT(username) DO UPDATE SET display_name=excluded.display_name,password_hash=excluded.password_hash,role='admin'";
        await using var cmd = new NpgsqlCommand(sql, conn); cmd.Parameters.AddWithValue("u", username); cmd.Parameters.AddWithValue("d", display); cmd.Parameters.AddWithValue("h", hash); await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"Admin '{username}' configured successfully.");
    }

    private static string ReadPassword()
    {
        var chars = new List<char>();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && chars.Count > 0) { chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray());
    }
}
