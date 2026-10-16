namespace Y3L.Supply.Api;

/// <summary>A 4xx problem with a fixed message — mirrors FastAPI's HTTPException.</summary>
public class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static ApiException BadRequest(string msg) => new(400, msg);
    public static ApiException NotFound(string msg) => new(404, msg);
    public static ApiException Unprocessable(string msg) => new(422, msg);
}

/// <summary>Returns {"detail": "..."} for ApiException, like FastAPI.</summary>
public class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex)
        {
            ctx.Response.StatusCode = ex.Status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "unhandled error");
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { detail = "internal error" });
        }
    }
}
