using System.Diagnostics;
using DeckCapture.Domain;

namespace DeckCapture.Delivery;

// Both buckets are debited together; a canceled waiter never reserves aggregate
// capacity that another transfer could use. Every destination byte uses this gate.
internal sealed class TransferLimiter
{
    internal const int ChunkSize = 64 * 1024;
    private readonly object gate = new();
    private readonly Bucket aggregate = new();

    internal sealed class Bucket
    {
        internal double Tokens;
        internal double Rate;
        internal long Updated = Stopwatch.GetTimestamp();

        internal void Update(double rate, long now)
        {
            Tokens = Math.Min(ChunkSize, Tokens + Stopwatch.GetElapsedTime(Updated, now).TotalSeconds * Rate);
            Updated = now;
            Rate = rate;
        }
    }

    internal async Task TakeAsync(Bucket transfer, int count, Func<DeliveryRates> rates, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = rates();
            if (!double.IsFinite(current.PerTransferMiBps) || current.PerTransferMiBps <= 0 ||
                !double.IsFinite(current.AggregateMiBps) || current.AggregateMiBps <= 0)
                throw new ArgumentOutOfRangeException(nameof(rates), "Transfer and aggregate speeds must be finite and positive, including outside windows.");
            double delay;
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                transfer.Update(current.PerTransferMiBps * 1048576, now);
                aggregate.Update(current.AggregateMiBps * 1048576, now);
                if (transfer.Tokens >= count && aggregate.Tokens >= count)
                {
                    transfer.Tokens -= count;
                    aggregate.Tokens -= count;
                    return;
                }
                delay = Math.Max((count - transfer.Tokens) / transfer.Rate, (count - aggregate.Tokens) / aggregate.Rate);
            }
            // Re-read settings often so an already-running copy slows at closure.
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay, .001, .05)), cancellationToken);
        }
    }
}
