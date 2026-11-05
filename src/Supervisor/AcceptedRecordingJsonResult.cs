using System.Text.Json;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace DeckCapture.Supervisor;

/// <summary>Finish an accepted recording response inside the idempotency buffer even if its browser disconnects.</summary>
public sealed class AcceptedRecordingJsonResult(object value) : IResult
{
    public Task ExecuteAsync(HttpContext context) => WriteAsync(context, value);

    public static Task WriteAsync(HttpContext context, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        context.Response.ContentType = "application/json; charset=utf-8";
        var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        // WriteAsJsonAsync(None) substitutes RequestAborted. Serialize directly to the
        // middleware's MemoryStream instead: its full receipt must precede any wire write.
        // This changes neither command admission/ownership cancellation nor the HTTP token.
        return JsonSerializer.SerializeAsync(context.Response.Body, value, value.GetType(), options, CancellationToken.None);
    }
}
