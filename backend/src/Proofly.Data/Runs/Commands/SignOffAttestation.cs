using System.Security.Cryptography;
using System.Text;

namespace Proofly.Data.Runs.Commands;

/// <summary>
/// Pure, deterministic sign-off hashing and attestation text (no I/O, no clock). The attestation hash binds the approval to the
/// exact statement, the run's result hash, the state of every exception and the ruleset version at the time of approval.
/// </summary>
public static class SignOffAttestation
{
    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Text(string runId, string legalEntityId, DateOnly businessDate, string rulesetVersion, string? resultHash, IReadOnlyList<string> qualifications)
    {
        var sb = new StringBuilder();
        sb.Append($"I attest that I have reviewed run {runId} for legal entity {legalEntityId} and business date {businessDate:yyyy-MM-dd} ")
          .Append($"(ruleset {rulesetVersion}, result {(resultHash is null ? "unavailable" : resultHash[..Math.Min(12, resultHash.Length)])}). ")
          .Append("All mandatory controls are evidenced and no blocking exception is unresolved.");
        if (qualifications.Count > 0) { sb.Append(" Qualifications: ").Append(string.Join("; ", qualifications)).Append('.'); }
        return sb.ToString();
    }

    private static string Lines(IEnumerable<string> lines) => Sha256Hex(string.Join('\n', lines));

    /// <summary>Hash of the ids of unresolved exceptions (the open set), ordinally sorted.</summary>
    public static string OpenSetHash(IEnumerable<(string Id, string Status, bool Blocking)> exceptions, Func<string, bool> isOpen) =>
        Lines(exceptions.Where(e => isOpen(e.Status)).Select(e => e.Id).Order(StringComparer.Ordinal));

    /// <summary>Hash of id, status and blocking flag of every exception of the run, ordinally sorted by id.</summary>
    public static string ExceptionStateHash(IEnumerable<(string Id, string Status, bool Blocking)> exceptions) =>
        Lines(exceptions.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => $"{e.Id}|{e.Status}|{(e.Blocking ? 1 : 0)}"));

    public static string AttestationHash(string attestationTextHash, string? resultHash, string exceptionStateHash, string rulesetVersion) =>
        Lines([attestationTextHash, resultHash ?? "", exceptionStateHash, rulesetVersion]);
}
