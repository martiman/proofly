using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Safeguarding;

/// <summary>Safeguarding-specific reason codes (not in the shared FindingCodes).</summary>
public static class SafeguardingCodes
{
    public const string ExternalBalanceMissing = "EXTERNAL_BALANCE_MISSING";
    public const string AdjustmentUnapproved = "ADJUSTMENT_UNAPPROVED";
}

/// <summary>Independently supported client-account balance (one per client account).</summary>
public sealed record ClientObligationInput(string AccountId, string Currency, decimal IndependentAmount);

/// <summary>External safeguarding balance with eligibility classification. Eligible=false carries an IneligibleReason.</summary>
public sealed record ExternalBalanceInput(
    string SourceRowId, string ExternalAccountId, Money Balance, DateTimeOffset SourceTimestamp,
    bool Eligible, string? IneligibleReason = null);

/// <summary>Signed adjustment (positive increases coverage). Counts only if Approved and not expired at the business date.</summary>
public sealed record AdjustmentInput(string AdjustmentId, Money Amount, AdjustmentStatus Status, DateOnly? ExpiresOn = null);

public sealed record SafeguardingOptions
{
    public string RuleVersion { get; init; } = "safeguarding-1.0";
    /// <summary>A balance older than this (relative to AsOf) is stale.</summary>
    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Stale balances are excluded from eligible coverage (conservative).</summary>
    public bool ExcludeStaleBalances { get; init; } = true;
    public Severity StaleSeverity { get; init; } = Severity.High;
    public bool StaleBlocking { get; init; }
    /// <summary>Shortfall / obligation at or above this ratio is Critical, otherwise High.</summary>
    public decimal CriticalShortfallRatio { get; init; } = 0.10m;
}

public sealed record ObligationLine(string AccountId, Money Amount);

/// <summary>Included/excluded external balance line for the UI. ExclusionReason is null when included.</summary>
public sealed record BalanceLine(string SourceRowId, string ExternalAccountId, Money Balance, DateTimeOffset SourceTimestamp, bool Included, string? ExclusionReason);

public sealed record AdjustmentLine(string AdjustmentId, Money Amount, bool Included, string? ExclusionReason);

public sealed record CurrencyPosition(
    string Currency, Money Obligation, Money EligibleTotal, Money ApprovedAdjustments,
    Money? Position, SafeguardingState State,
    IReadOnlyList<ObligationLine> ObligationLines, IReadOnlyList<BalanceLine> BalanceLines, IReadOnlyList<AdjustmentLine> AdjustmentLines);

public sealed record SafeguardingEvaluation(ControlResult Control, IReadOnlyList<CurrencyPosition> Positions);

/// <summary>Pure safeguarding coverage control: coverage = eligible + approved adjustments - obligation, per currency, never netted.</summary>
public static class SafeguardingCoverageControl
{
    public static SafeguardingEvaluation Evaluate(
        DateTimeOffset asOf, DateOnly businessDate,
        IEnumerable<ClientObligationInput> obligations, IEnumerable<ExternalBalanceInput> balances,
        IEnumerable<AdjustmentInput> adjustments, SafeguardingOptions options)
    {
        var obl = obligations.ToList();
        var bal = balances.ToList();
        var adj = adjustments.ToList();
        var currencies = obl.Select(o => o.Currency).Concat(bal.Select(b => b.Balance.Currency))
            .Concat(adj.Select(a => a.Amount.Currency)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal);

        var findings = new List<Finding>();
        var positions = new List<CurrencyPosition>();
        var rv = options.RuleVersion;

        Finding F(string key, string code, Severity sev, string desc, IEnumerable<RecordRef> refs, Money? value, bool blocking) =>
            new(key, ControlArea.Safeguarding, code, sev, desc, refs.ToList(), value, rv, blocking);

        foreach (var ccy in currencies)
        {
            var oLines = obl.Where(o => o.Currency == ccy).OrderBy(o => o.AccountId, StringComparer.Ordinal)
                .Select(o => new ObligationLine(o.AccountId, new Money(o.IndependentAmount, ccy))).ToList();
            var obligation = new Money(oLines.Sum(l => l.Amount.Amount), ccy);
            foreach (var l in oLines.Where(l => l.Amount.Amount < 0))
            {
                findings.Add(F($"safeguarding:INVALID_VALUE:client_account:{ccy}:{l.AccountId}", FindingCodes.InvalidValue, Severity.Medium,
                    $"Negative independent client balance {l.Amount} for account {l.AccountId}; counted as supplied.",
                    [new RecordRef("client_account", l.AccountId)], l.Amount, false));
            }

            var bLines = new List<BalanceLine>();
            var ownBalances = bal.Where(b => b.Balance.Currency == ccy).OrderBy(b => b.SourceRowId, StringComparer.Ordinal).ToList();
            foreach (var b in ownBalances)
            {
                var rref = new[] { new RecordRef("safeguarding_balances", b.SourceRowId) };
                string? reason = null;
                if (b.Balance.Amount < 0)
                {
                    reason = FindingCodes.InvalidValue;
                    findings.Add(F($"safeguarding:INVALID_VALUE:safeguarding_balances:{b.SourceRowId}", FindingCodes.InvalidValue, Severity.High,
                        $"Negative safeguarding balance {b.Balance} requires explicit policy; excluded.", rref, b.Balance, false));
                }
                else if (!b.Eligible)
                {
                    reason = "ACCOUNT_NOT_ELIGIBLE" + (b.IneligibleReason is null ? "" : ": " + b.IneligibleReason);
                }
                else if (asOf - b.SourceTimestamp > options.StaleThreshold)
                {
                    findings.Add(F($"safeguarding:EXTERNAL_BALANCE_STALE:safeguarding_balances:{b.SourceRowId}", FindingCodes.ExternalBalanceStale,
                        options.StaleSeverity,
                        $"Balance {b.Balance} on {b.ExternalAccountId} is dated {b.SourceTimestamp:O}, older than the {options.StaleThreshold} freshness threshold.",
                        rref, b.Balance, options.StaleBlocking));
                    if (options.ExcludeStaleBalances) reason = FindingCodes.ExternalBalanceStale;
                }
                bLines.Add(new BalanceLine(b.SourceRowId, b.ExternalAccountId, b.Balance, b.SourceTimestamp, reason is null, reason));
            }
            var eligible = new Money(bLines.Where(l => l.Included).Sum(l => l.Balance.Amount), ccy);

            var aLines = new List<AdjustmentLine>();
            foreach (var a in adj.Where(a => a.Amount.Currency == ccy).OrderBy(a => a.AdjustmentId, StringComparer.Ordinal))
            {
                string? reason = null;
                if (a.Status != AdjustmentStatus.Approved)
                {
                    reason = "ADJUSTMENT_UNAPPROVED";
                    if (a.Status == AdjustmentStatus.Proposed)
                    {
                        findings.Add(F($"safeguarding:ADJUSTMENT_UNAPPROVED:adjustment:{a.AdjustmentId}", SafeguardingCodes.AdjustmentUnapproved, Severity.Medium,
                            $"Adjustment {a.AdjustmentId} of {a.Amount} is not approved and is excluded from coverage.",
                            [new RecordRef("adjustment", a.AdjustmentId)], a.Amount, false));
                    }
                }
                else if (a.ExpiresOn is { } exp && exp < businessDate)
                {
                    reason = "ADJUSTMENT_EXPIRED";
                }
                aLines.Add(new AdjustmentLine(a.AdjustmentId, a.Amount, reason is null, reason));
            }
            var adjTotal = new Money(aLines.Where(l => l.Included).Sum(l => l.Amount.Amount), ccy);

            Money? position = null;
            SafeguardingState state;
            if (obligation.Amount > 0 && ownBalances.Count == 0)
            {
                state = SafeguardingState.Qualified;
                findings.Add(F($"safeguarding:EXTERNAL_BALANCE_MISSING:{ccy}", SafeguardingCodes.ExternalBalanceMissing, Severity.Critical,
                    $"No external safeguarding balance for {ccy} while an obligation of {obligation} exists; coverage cannot be established (not zero coverage).",
                    oLines.Select(l => new RecordRef("client_account", l.AccountId)), obligation, true));
            }
            else
            {
                position = eligible + adjTotal - obligation;
                if (position.Value.Amount < 0)
                {
                    state = SafeguardingState.Shortfall;
                    var ratio = obligation.Amount == 0 ? 1m : -position.Value.Amount / obligation.Amount;
                    findings.Add(F($"safeguarding:SHORTFALL:{ccy}", FindingCodes.Shortfall,
                        ratio >= options.CriticalShortfallRatio ? Severity.Critical : Severity.High,
                        $"Eligible coverage {eligible + adjTotal} is below client obligation {obligation} by {position.Value.Negate()}.",
                        bLines.Where(l => l.Included).Select(l => new RecordRef("safeguarding_balances", l.SourceRowId))
                            .Concat(oLines.Select(l => new RecordRef("client_account", l.AccountId))),
                        position, true));
                }
                else if (position.Value.Amount > 0) state = SafeguardingState.Surplus;
                else state = obligation.Amount == 0 ? SafeguardingState.NoObligation : SafeguardingState.Covered;
            }
            positions.Add(new CurrencyPosition(ccy, obligation, eligible, adjTotal, position, state, oLines, bLines, aLines));
        }

        return new SafeguardingEvaluation(ControlResult.From(ControlArea.Safeguarding, rv, findings), positions);
    }
}
