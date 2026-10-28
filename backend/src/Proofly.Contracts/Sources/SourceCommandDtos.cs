namespace Proofly.Contracts.Sources;

/// <summary>Body of retryDeliveryIngestion (ReasonOnly).</summary>
public sealed record ReasonRequestDto(string? Reason);

/// <summary>Body of markDeliverySuperseded.</summary>
public sealed record MarkSupersededRequestDto(string? SupersededByDeliveryId, string? Reason);
