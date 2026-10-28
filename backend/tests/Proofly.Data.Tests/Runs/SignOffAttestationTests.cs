using Proofly.Data.Runs.Commands;

namespace Proofly.Data.Tests.Runs;

public class SignOffAttestationTests
{
    private static readonly (string, string, bool)[] Set = [("E-2", "OPEN", true), ("E-1", "RESOLVED", false), ("E-3", "IN_INVESTIGATION", false)];
    private static bool IsOpen(string s) => s is not ("RESOLVED" or "ACCEPTED_DIFFERENCE" or "FALSE_POSITIVE");

    [Fact]
    public void Hashes_are_order_independent_and_deterministic()
    {
        var shuffled = Set.Reverse().ToArray();
        Assert.Equal(SignOffAttestation.ExceptionStateHash(Set), SignOffAttestation.ExceptionStateHash(shuffled));
        Assert.Equal(SignOffAttestation.OpenSetHash(Set, IsOpen), SignOffAttestation.OpenSetHash(shuffled, IsOpen));
        Assert.Matches("^[0-9a-f]{64}$", SignOffAttestation.ExceptionStateHash(Set));
    }

    [Fact]
    public void A_status_change_moves_the_state_hash_and_closing_moves_the_open_set()
    {
        var changed = new[] { ("E-2", "OPEN", true), ("E-1", "OPEN", false), ("E-3", "IN_INVESTIGATION", false) };
        Assert.NotEqual(SignOffAttestation.ExceptionStateHash(Set), SignOffAttestation.ExceptionStateHash(changed));
        Assert.NotEqual(SignOffAttestation.OpenSetHash(Set, IsOpen), SignOffAttestation.OpenSetHash(changed, IsOpen));
        var inProgress = new[] { ("E-2", "IN_INVESTIGATION", true), ("E-1", "RESOLVED", false), ("E-3", "IN_INVESTIGATION", false) };
        Assert.Equal(SignOffAttestation.OpenSetHash(Set, IsOpen), SignOffAttestation.OpenSetHash(inProgress, IsOpen));
        Assert.NotEqual(SignOffAttestation.ExceptionStateHash(Set), SignOffAttestation.ExceptionStateHash(inProgress));
    }

    [Fact]
    public void Attestation_hash_binds_text_result_exceptions_and_ruleset()
    {
        var text = SignOffAttestation.Text("RUN-1", "LE-1", new DateOnly(2026, 1, 2), "rs1", new string('a', 64), []);
        var th = SignOffAttestation.Sha256Hex(text);
        var baseHash = SignOffAttestation.AttestationHash(th, new string('a', 64), "s", "rs1");
        Assert.Equal(baseHash, SignOffAttestation.AttestationHash(th, new string('a', 64), "s", "rs1"));
        Assert.NotEqual(baseHash, SignOffAttestation.AttestationHash(th, new string('b', 64), "s", "rs1"));
        Assert.NotEqual(baseHash, SignOffAttestation.AttestationHash(th, new string('a', 64), "t", "rs1"));
        Assert.NotEqual(baseHash, SignOffAttestation.AttestationHash(th, new string('a', 64), "s", "rs2"));
        Assert.Contains("qualified", SignOffAttestation.Text("R", "L", new DateOnly(2026, 1, 2), "rs1", null, ["matching is qualified"]));
    }
}
