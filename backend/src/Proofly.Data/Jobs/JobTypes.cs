using System.Text.Json;

namespace Proofly.Data.Jobs;

public static class JobState
{
    public const string Queued = "QUEUED", Running = "RUNNING", Succeeded = "SUCCEEDED", Failed = "FAILED", Dead = "DEAD";
    public static readonly string[] All = [Queued, Running, Succeeded, Failed, Dead];
}

/// <summary>A job to enqueue. <see cref="DedupeKey"/> makes enqueue idempotent (same key = same job).</summary>
public sealed record JobRequest(
    string Type,
    object? Payload = null,
    string? DedupeKey = null,
    int MaxAttempts = 5,
    DateTimeOffset? RunAfter = null,
    string? RunId = null,
    string? DeliveryId = null);

public sealed record EnqueueResult(long JobId, bool Created);

/// <summary>A claimed job as seen by the worker.</summary>
public sealed record ClaimedJob(long Id, string Type, string PayloadJson, int Attempts, int MaxAttempts, string? RunId, string? DeliveryId);

public sealed record JobContext(long JobId, int Attempt, int MaxAttempts, string? RunId, string? DeliveryId);

public interface IJobHandler<in TPayload>
{
    Task HandleAsync(TPayload payload, JobContext context, CancellationToken ct);
}

/// <summary>Throw from a handler to dead-letter immediately (no retry).</summary>
public sealed class PermanentJobException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class JobOptions
{
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(15);
    public int Concurrency { get; set; } = 2;

    /// <summary>Delay before attempt n+1 after attempt n failed: base * 2^(n-1), capped.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var seconds = Math.Min(BackoffBase.TotalSeconds * factor, BackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}

internal static class JobJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
