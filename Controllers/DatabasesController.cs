using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vestrom.Api.Models;
using Vestrom.Api.Services;

namespace Vestrom.Api.Controllers;

[ApiController, Authorize(Roles = "admin"), Route("api/databases")]
public sealed class DatabasesController(PostgreSqlService postgres) : ControllerBase
{
    [HttpGet]
    public Task<DatabaseInfo[]> Get(CancellationToken ct) => postgres.GetDatabasesAsync(ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<DatabaseDetail>> Get(string id, CancellationToken ct)
    {
        try { return Ok(await postgres.GetDatabaseAsync(id, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(new { detail = ex.Message }); }
    }

    [HttpPost("query")]
    public async Task<ActionResult<QueryResult>> Query([FromBody] QueryRequest request, CancellationToken ct)
    {
        try { return Ok(await postgres.ExecuteQueryAsync(request.DatabaseId, request.Sql, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { detail = ex.Message }); }
        catch (Npgsql.NpgsqlException ex) { return BadRequest(new { detail = ex.Message }); }
    }
}

public record QueryRequest(string DatabaseId, string Sql);
