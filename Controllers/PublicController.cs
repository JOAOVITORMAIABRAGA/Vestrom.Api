using Microsoft.AspNetCore.Mvc;
using Vestrom.Api.Models;
using Vestrom.Api.Services;

namespace Vestrom.Api.Controllers;

[ApiController, Route("api/public")]
public sealed class PublicController(SystemMetricsService system, PostgreSqlService postgres) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<DashboardData>> Status(CancellationToken ct)
    {
        var s = await system.CollectAsync(ct); var pg = await postgres.GetInfoAsync(ct);
        var dbs = pg.Status == "online" ? await postgres.GetDatabasesAsync(ct) : Array.Empty<DatabaseInfo>();
        var services = s.Server.Services.Select(x => x.Name == "PostgreSQL" ? x with { Status = pg.Status, Version = pg.Version } : x).ToArray();
        var server = s.Server with { Services = services };
        return Ok(new DashboardData(server, pg, s.Resources, dbs, s.Battery, s.TemperatureC, DateTimeOffset.UtcNow));
    }
}
