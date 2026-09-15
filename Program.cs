using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Vestrom.Api.Configuration;
using Vestrom.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Vestrom").Get<VestromOptions>() ?? new VestromOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SystemMetricsService>();
builder.Services.AddSingleton<PostgreSqlService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

if (string.IsNullOrWhiteSpace(options.JwtKey) || options.JwtKey.Length < 32)
    throw new InvalidOperationException("Configure Vestrom:JwtKey with a random secret of at least 32 characters.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
{
    jwt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtKey)),
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();

if (options.CorsOrigins.Length > 0)
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(options.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (args.Any(x => string.Equals(x, "--create-admin", StringComparison.OrdinalIgnoreCase)))
{
    using var scope = app.Services.CreateScope();
    await AdminSetup.RunAsync(builder.Configuration, scope.ServiceProvider.GetRequiredService<AuthService>());
    return;
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/public/health", () => Results.Ok(new { status = "online", service = "Vestrom Database", timestamp = DateTimeOffset.UtcNow }));
app.Run();
