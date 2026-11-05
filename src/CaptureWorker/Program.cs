using System.Text.Json;
using DeckCapture.Capture;
using DeckCapture.Domain;

if (args.Length != 1) return 2;
try
{
    var options = JsonSerializer.Deserialize<CaptureOpenOptions>(await File.ReadAllTextAsync(args[0]))
        ?? throw new InvalidDataException("Missing capture options.");
    return await CaptureWorkerHost.RunAsync(options);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
