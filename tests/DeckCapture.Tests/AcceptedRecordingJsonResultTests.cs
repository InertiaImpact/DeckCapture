using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace DeckCapture.Tests;

public sealed class AcceptedRecordingJsonResultTests
{
    [Fact]
    public async Task CanceledBrowserStillProducesCompleteConfiguredRecordingJsonWithoutChangingItsToken()
    {
        using var response = new ResponseFixture();
        var originalToken = response.Context.RequestAborted;
        response.Context.Response.StatusCode = StatusCodes.Status202Accepted;
        var job = Job();

        await new AcceptedRecordingJsonResult(job).ExecuteAsync(response.Context);

        Assert.Equal(originalToken, response.Context.RequestAborted);
        Assert.True(response.Context.RequestAborted.IsCancellationRequested);
        Assert.Equal(StatusCodes.Status202Accepted, response.Context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Context.Response.ContentType);
        Assert.Equal(JsonSerializer.Serialize(job, response.Options), response.Json);
        using var document = JsonDocument.Parse(response.Json);
        Assert.Equal("Recording", document.RootElement.GetProperty("state").GetString());
        Assert.Equal(job.FileName, document.RootElement.GetProperty("fileName").GetString());
        Assert.False(document.RootElement.TryGetProperty("FileName", out _));
        Assert.Equal("Source title", document.RootElement.GetProperty("metadata").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status409Conflict)]
    public async Task BufferedErrorKeepsFullRuntimeFieldsAndStatusWhenBrowserIsCanceled(int status)
    {
        using var response = new ResponseFixture();
        response.Context.Response.StatusCode = status;
        object error = new { Error = "A \"quoted\" failure", JobId = "retained-job", Retryable = false };

        await AcceptedRecordingJsonResult.WriteAsync(response.Context, error);

        Assert.Equal(status, response.Context.Response.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(error, error.GetType(), response.Options), response.Json);
        using var document = JsonDocument.Parse(response.Json);
        Assert.Equal("retained-job", document.RootElement.GetProperty("jobId").GetString());
        Assert.False(document.RootElement.GetProperty("retryable").GetBoolean());
        Assert.True(response.Context.RequestAborted.IsCancellationRequested);
    }

    [Fact]
    public async Task CompleteCanceledClientResponseCanBePersistedAndReplayedExactlyAcrossStoreRestart()
    {
        using var accepted = new ResponseFixture();
        await new AcceptedRecordingJsonResult(Job()).ExecuteAsync(accepted.Context);
        // Keep the isolated database as diagnostic evidence; this test never touches live state.
        var directory = Path.Combine(Path.GetTempPath(), "DeckCapture-accepted-json-tests", Guid.NewGuid().ToString("N"));
        var store = new StateStore(directory);
        store.SaveRequest("one-click-test-key", "POST /api/recordings/prepare", "same-body-sha256", 200, accepted.Json);
        var saved = new StateStore(directory).ReadRequest("one-click-test-key")!.Value;
        using var replay = new ResponseFixture(canceled: false);
        replay.Context.Response.StatusCode = saved.Status;
        replay.Context.Response.ContentType = "application/json";
        await replay.Context.Response.WriteAsync(saved.Response);

        Assert.Equal(accepted.Buffer.ToArray(), replay.Buffer.ToArray());
        Assert.Equal("POST /api/recordings/prepare", saved.Path);
        Assert.Equal("same-body-sha256", saved.Hash);
        using var document = JsonDocument.Parse(replay.Json);
        Assert.Equal("accepted-job", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("Recording", document.RootElement.GetProperty("state").GetString());
    }

    private static RecordingJob Job() => new()
    {
        Id = "accepted-job", ChannelId = "ch1", FileName = "Tape \"A\" – café", State = JobState.Recording,
        Metadata = new() { ["title"] = "Source title" }, Revision = 2
    };

    private sealed class ResponseFixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _abort = new();
        public MemoryStream Buffer { get; } = new();
        public DefaultHttpContext Context { get; }
        public JsonSerializerOptions Options => _services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        public string Json => Encoding.UTF8.GetString(Buffer.ToArray());

        public ResponseFixture(bool canceled = true)
        {
            var services = new ServiceCollection();
            services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            _services = services.BuildServiceProvider();
            if (canceled) _abort.Cancel();
            Context = new DefaultHttpContext { RequestServices = _services, RequestAborted = _abort.Token };
            Context.Response.Body = Buffer;
        }

        public void Dispose() { Buffer.Dispose(); _abort.Dispose(); _services.Dispose(); }
    }
}
