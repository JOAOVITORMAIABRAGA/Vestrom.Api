using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Vestrom.Api.Configuration;
using Vestrom.Api.Models;

namespace Vestrom.Api.Services;

public sealed class AuthService(IConfiguration configuration, VestromOptions options)
{
    private readonly PasswordHasher<string> _hasher = new();
    private string ConnectionString => configuration.GetConnectionString("PostgreSQL") ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            CREATE TABLE IF NOT EXISTS vestrom_users (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                username text NOT NULL UNIQUE,
                display_name text NOT NULL,
                password_hash text NOT NULL,
                role text NOT NULL CHECK (role IN ('admin','viewer')),
                created_at timestamptz NOT NULL DEFAULT now(),
                last_login_at timestamptz NULL
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Session?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, username, display_name, password_hash, role FROM vestrom_users WHERE username=@username", conn);
        cmd.Parameters.AddWithValue("username", username.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var id = r.GetGuid(0).ToString(); var user = r.GetString(1); var display = r.GetString(2); var hash = r.GetString(3); var role = r.GetString(4);
        if (_hasher.VerifyHashedPassword(user, hash, password) == PasswordVerificationResult.Failed) return null;
        await r.CloseAsync();
        await using var update = new NpgsqlCommand("UPDATE vestrom_users SET last_login_at=now() WHERE id=@id", conn); update.Parameters.AddWithValue("id", Guid.Parse(id)); await update.ExecuteNonQueryAsync(ct);
        var auth = new AuthUser(id, user, role, display);
        return new Session(CreateToken(auth), auth);
    }

    public async Task<AuthUser?> GetUserAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var id = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(id, out var guid)) return null;
        await using var conn = new NpgsqlConnection(ConnectionString); await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT username, display_name, role FROM vestrom_users WHERE id=@id", conn); cmd.Parameters.AddWithValue("id", guid);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        return new AuthUser(guid.ToString(), r.GetString(0), r.GetString(2), r.GetString(1));
    }

    private string CreateToken(AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(options.JwtKey) || options.JwtKey.Length < 32) throw new InvalidOperationException("Vestrom:JwtKey must be configured with at least 32 characters.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(options.JwtMinutes), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string HashPassword(string username, string password) => _hasher.HashPassword(username, password);
}
