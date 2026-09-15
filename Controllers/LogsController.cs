using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vestrom.Api.Models;

namespace Vestrom.Api.Controllers;

[ApiController, Authorize, Route("api/logs")]
public sealed class LogsController(ILogger<LogsController> logger) : ControllerBase
{
    [HttpGet]
    public ActionResult<LogEntry[]> Get()
    {
        // The API intentionally does not pretend to expose PostgreSQL's log files. Host-specific log collection will be added once a configured log source is available.
        return Ok(Array.Empty<LogEntry>());
    }
}
