var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/api/public/health", () =>
{
    return Results.Ok(new
    {
        status = "online",
        service = "Vestrom Database",
        timestamp = DateTime.UtcNow
    });
});

app.Run();
