using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vestrom.Api.Models;
using Vestrom.Api.Services;

namespace Vestrom.Api.Controllers;

[ApiController, Authorize, Route("api/dashboard")]
public sealed class DashboardController(SystemMetricsService system, PostgreSqlService postgres) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardData>> Get(CancellationToken ct)
    {
        var s = await system.CollectAsync(ct); var pg = await postgres.GetInfoAsync(ct); var dbs = pg.Status == "online" ? await postgres.GetDatabasesAsync(ct) : Array.Empty<DatabaseInfo>();
        var services = s.Server.Services.Select(x => x.Name == "PostgreSQL" ? x with { Status = pg.Status, Version = pg.Version, Uptime = pg.Uptime } : x).ToArray();
        return Ok(new DashboardData(s.Server with { Services = services }, pg, s.Resources, dbs, s.Battery, s.TemperatureC, DateTimeOffset.UtcNow));
    }
}
