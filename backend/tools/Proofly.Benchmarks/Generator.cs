using System.Globalization;
using System.Text;

namespace Proofly.Benchmarks;

/// <summary>One generated delivery file with the manifest facts a registration needs.</summary>
public sealed record GeneratedFile(string Source, string DeliveryId, string Path, int Rows, IReadOnlyDictionary<string, decimal> Totals, long Bytes);

public sealed record GenerationSummary(
    double Scale, int Accounts, int Transactions, IReadOnlyList<GeneratedFile> Files, IReadOnlyDictionary<string, int> Seeded);

/// <summary>
/// Deterministic synthetic delivery generator (seeded RNG, same output for the same scale and seed). Writes CSV files in the
/// column layout of examples/synthetic-data/2026-10-27 to a directory outside the repository. Transactions are split
/// 45% transfers / 20% direct debits / 35% card journeys; every transfer and direct debit also produces a ledger entry and a
/// bank transaction; card journeys (authorisation + presentment) are settled in batches of about 50 with one bank credit each.
/// One legal entity, three currencies (GBP 45%, EUR 40%, JPY 15% of accounts), 3 safeguarding accounts per currency.
/// Seeded defects (rates of the rows of the file they hit): ~0.5% missing required fields and ~0.5% duplicate rows in the
/// transaction sources, ~0.5% bank amounts that differ from the instruction, ~0.3% settled transfers without bank evidence,
/// ~0.3% settled transfers without a ledger entry (with <c>distinctRefs</c> every transfer additionally leaves an unmatched ledger and bank row), ~0.2% accounts whose recorded balance is wrong, one safeguarding row
/// without evidence reference (a GBP shortfall) and a JPY shortfall.
/// </summary>
public static class Generator
{
    public const string Entity = "LE-BENCH";
    public const string Date = "2026-10-27";
    public const int Seed = 20261027;
    private static readonly string[] Ccys = ["GBP", "EUR", "JPY"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Sink : IDisposable
    {
        private readonly StreamWriter _w;
        public readonly string Source, Path;
        public int Rows;
        public readonly Dictionary<string, decimal> Totals = [];
        public Sink(string dir, string source, string header)
        {
            Source = source;
            Path = System.IO.Path.Combine(dir, source + ".csv");
            _w = new StreamWriter(Path, false, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
            _w.WriteLine(header);
        }
        public void Row(decimal amount, string ccy, params string[] f)
        {
            _w.WriteLine(string.Join(',', f));
            Rows++;
            Totals[ccy] = Totals.GetValueOrDefault(ccy) + amount;
        }
        public void Dispose() => _w.Dispose();
    }

    private static string A(decimal d, string ccy) => ccy == "JPY" ? decimal.Round(d, 0).ToString("0", Inv) : d.ToString("0.00", Inv);
    private static string Ts(int hour, int minute, int second = 0, int dayOffset = 0) =>
        new DateTimeOffset(2026, 10, 27, hour, minute, second, TimeSpan.FromHours(1)).AddDays(dayOffset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv);

    public static GenerationSummary Generate(string dir, double scale, bool distinctRefs = false)
    {
        Directory.CreateDirectory(dir);
        var rng = new Random(Seed);
        var n = (int)Math.Round(100_000 * scale);
        var accounts = (int)Math.Round(100_000 * scale);
        bool Hit(double rate) => rng.NextDouble() < rate;
        var seeded = new Dictionary<string, int>();
        void Seed_(string k) => seeded[k] = seeded.GetValueOrDefault(k) + 1;

        string[] acctCcy = new string[accounts];
        var pool = Ccys.ToDictionary(c => c, _ => new List<int>());
        for (var i = 0; i < accounts; i++)
        {
            var m = i % 100;
            acctCcy[i] = m < 45 ? "GBP" : m < 85 ? "EUR" : "JPY";
            pool[acctCcy[i]].Add(i);
        }
        string Acct(int i) => $"CA-{i + 1:D6}";
        int PickAccount(string ccy) { var p = pool[ccy]; return p[rng.Next(p.Count)]; }
        decimal Amt(string ccy) => ccy == "JPY" ? rng.Next(500, 200_000) : rng.Next(500, 200_000) / 100m;
        var opening = new decimal[accounts];
        var net = new decimal[accounts];
        for (var i = 0; i < accounts; i++) opening[i] = acctCcy[i] == "JPY" ? rng.Next(500_000, 5_000_000) : rng.Next(500_000, 5_000_000) / 100m;

        using var transfers = new Sink(dir, "transfer_instructions", "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference");
        using var dds = new Sink(dir, "direct_debit_events", "source_row_id,instruction_id,mandate_reference,client_account_id,legal_entity_id,amount,currency,collection_date,event_type,event_timestamp,source_status,return_reason,original_instruction_reference");
        using var cards = new Sink(dir, "card_transactions", "source_row_id,card_event_id,card_transaction_id,client_account_id,legal_entity_id,tokenized_card_reference,event_type,event_timestamp,amount,currency,debit_credit_indicator,merchant_reference,settlement_id,original_transaction_id,source_status");
        using var settlements = new Sink(dir, "card_settlements", "source_row_id,settlement_id,processor,legal_entity_id,settlement_date,gross_amount,fees,adjustments,net_amount,currency,destination_account,source_status");
        using var ledger = new Sink(dir, "internal_ledger_entries", "ledger_entry_id,account_id,legal_entity_id,booking_timestamp,value_date,debit_credit_indicator,amount,currency,transaction_reference,entry_type,source_status");
        using var bank = new Sink(dir, "banking_transactions", "external_transaction_id,external_account_id,booking_timestamp,value_date,amount,currency,debit_credit_indicator,bank_reference,transaction_status,source_timestamp");
        using var balances = new Sink(dir, "client_account_balances", "account_id,legal_entity_id,balance_date,balance_type,opening_balance_amount,balance_amount,currency,source_timestamp");
        using var sg = new Sink(dir, "safeguarding_balances", "source_row_id,external_account_id,legal_entity_id,balance_timestamp,balance_type,balance_amount,currency,evidence_reference,source_timestamp");

        var srcTs = Ts(5, 30, dayOffset: 1);
        var seqT = 0; var seqD = 0; var seqC = 0; var seqL = 0; var seqB = 0; var rowT = 0; var rowD = 0; var rowK = 0;

        void Ledger(string id, int acct, string dir_, decimal amount, string ccy, string reference, string type)
        {
            seqL++;
            var h = 6 + rng.Next(14); var mi = rng.Next(60);
            // a posted ledger entry always moves the recorded balance, even when its row is later rejected by ingestion
            net[acct] += dir_ == "CREDIT" ? amount : -amount;
            var account = Hit(0.004) ? "" : Acct(acct);
            if (account.Length == 0) Seed_("ledger.missing_account_id");
            ledger.Row(amount, ccy, id, account, Entity, Ts(h, mi), Date, dir_, A(amount, ccy), ccy, reference, type, "POSTED");
            if (Hit(0.003)) { ledger.Row(amount, ccy, id, Acct(acct), Entity, Ts(h, mi), Date, dir_, A(amount, ccy), ccy, reference, type, "POSTED"); Seed_("ledger.duplicate"); }
        }
        void Bank(string id, string ccy, decimal amount, string dir_, string reference)
        {
            seqB++;
            var h = 7 + rng.Next(13); var mi = rng.Next(60);
            bank.Row(amount, ccy, id, $"BANK-{ccy}-OPS", Ts(h, mi), Date, A(amount, ccy), ccy, dir_, reference, "BOOKED", srcTs);
            if (Hit(0.003)) { bank.Row(amount, ccy, id, $"BANK-{ccy}-OPS", Ts(h, mi), Date, A(amount, ccy), ccy, dir_, reference, "BOOKED", srcTs); Seed_("bank.duplicate"); }
        }

        var batchCcy = new List<string>();
        var batchSum = new List<decimal>();
        var batchCount = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var m = i % 100;
            if (m < 45)
            {
                // transfer instruction + ledger debit + bank debit
                seqT++;
                var ccy = Ccys[rng.Next(100) < 45 ? 0 : rng.Next(100) < 70 ? 1 : 2];
                var a = PickAccount(ccy);
                var amount = Amt(ccy);
                var submitted = Hit(0.01);
                var id = $"TRF-{seqT:D7}";
                // Default: instruction, ledger and bank share one reference (the join keys of the matching and lifecycle controls). With
                // distinctRefs the bank reference differs from the instruction id (as in the synthetic fixture), so ledger-to-bank cannot match.
                var reference = distinctRefs ? $"BANKREF-T-{seqT:D7}" : id;
                var h = 6 + rng.Next(14); var mi = rng.Next(60);
                var accountField = Hit(0.005) ? "" : Acct(a);
                if (accountField.Length == 0) Seed_("transfer.missing_client_account_id");
                transfers.Row(amount, ccy, $"SRC-T-{++rowT:D7}", id, accountField, Entity, "BANK_TRANSFER", A(amount, ccy), ccy, Ts(h, mi), Date,
                    submitted ? "SUBMITTED" : "SETTLED", reference, $"BENEF-{seqT % 1000}");
                if (Hit(0.005)) { transfers.Row(amount, ccy, $"SRC-T-{++rowT:D7}", id, Acct(a), Entity, "BANK_TRANSFER", A(amount, ccy), ccy, Ts(h, mi), Date, submitted ? "SUBMITTED" : "SETTLED", reference, $"BENEF-{seqT % 1000}"); Seed_("transfer.duplicate"); }
                if (submitted) { Seed_("transfer.submitted_no_bank"); continue; }
                if (Hit(0.003)) Seed_("transfer.settled_no_ledger"); else Ledger($"LED-T-{seqT:D7}", a, "DEBIT", amount, ccy, id, "TRANSFER");
                if (Hit(0.003)) { Seed_("transfer.settled_no_bank"); continue; }
                var bankAmount = amount;
                if (Hit(0.005)) { bankAmount = amount + (ccy == "JPY" ? 100 : 5.25m); Seed_("transfer.bank_value_mismatch"); }
                Bank($"BTX-T-{seqT:D7}", ccy, bankAmount, "DEBIT", reference);
            }
            else if (m < 65)
            {
                // direct debit (one SUBMITTED event, collected today) + ledger credit + bank credit
                seqD++;
                var ccy = Ccys[rng.Next(100) < 45 ? 0 : rng.Next(100) < 70 ? 1 : 2];
                var a = PickAccount(ccy);
                var amount = Amt(ccy);
                var id = $"DD-{seqD:D7}";
                var mandate = Hit(0.005) ? "" : $"MANDATE-{a + 1:D6}";
                if (mandate.Length == 0) Seed_("dd.missing_mandate");
                var evTs = Ts(16, rng.Next(60), dayOffset: -1);
                dds.Row(amount, ccy, $"SRC-D-{++rowD:D7}", id, mandate, Acct(a), Entity, A(amount, ccy), ccy, Date, "SUBMITTED", evTs, "SUBMITTED", "", "");
                if (Hit(0.005)) { dds.Row(amount, ccy, $"SRC-D-{++rowD:D7}", id, mandate, Acct(a), Entity, A(amount, ccy), ccy, Date, "SUBMITTED", evTs, "SUBMITTED", "", ""); Seed_("dd.duplicate"); }
                Ledger($"LED-D-{seqD:D7}", a, "CREDIT", amount, ccy, id, "DIRECT_DEBIT");
                var bankAmount = amount;
                if (Hit(0.005)) { bankAmount = amount + (ccy == "JPY" ? 100 : 5.25m); Seed_("dd.bank_value_mismatch"); }
                Bank($"BTX-D-{seqD:D7}", ccy, bankAmount, "CREDIT", id);
            }
            else
            {
                // card journey: authorisation + presentment in a settlement batch of ~50 journeys of one currency
                seqC++;
                var b = (seqC - 1) / 50;
                if (b >= batchCcy.Count) { batchCcy.Add(Ccys[rng.Next(100) < 45 ? 0 : rng.Next(100) < 70 ? 1 : 2]); batchSum.Add(0); batchCount.Add(0); }
                var ccy = batchCcy[b];
                var a = PickAccount(ccy);
                var amount = Amt(ccy);
                batchSum[b] += amount; batchCount[b]++;
                var ctx = $"CTX-{seqC:D7}"; var settle = $"CARD-{b + 1:D5}";
                var h = 6 + rng.Next(12); var mi = rng.Next(60);
                cards.Row(amount, ccy, $"SRC-K-{++rowK:D7}", $"CEV-A-{seqC:D7}", ctx, Acct(a), Entity, $"CARD-TOKEN-{a + 1:D6}", "AUTHORIZATION", Ts(h, mi), A(amount, ccy), ccy, "DEBIT", $"MERCH-{seqC % 500}", "", "", "AUTHORIZED");
                cards.Row(amount, ccy, $"SRC-K-{++rowK:D7}", $"CEV-P-{seqC:D7}", ctx, Acct(a), Entity, $"CARD-TOKEN-{a + 1:D6}", "PRESENTMENT", Ts(h + 1, mi), A(amount, ccy), ccy, "DEBIT", $"MERCH-{seqC % 500}", settle, "", "PRESENTED");
                if (Hit(0.005)) { cards.Row(amount, ccy, $"SRC-K-{++rowK:D7}", $"CEV-P-{seqC:D7}", ctx, Acct(a), Entity, $"CARD-TOKEN-{a + 1:D6}", "PRESENTMENT", Ts(h + 1, mi), A(amount, ccy), ccy, "DEBIT", $"MERCH-{seqC % 500}", settle, "", "PRESENTED"); Seed_("card.duplicate"); }
            }
        }

        // Settlements and their bank credits.
        for (var b = 0; b < batchCcy.Count; b++)
        {
            var ccy = batchCcy[b];
            var gross = batchSum[b];
            var fees = ccy == "JPY" ? decimal.Round(gross * 0.015m, 0) : decimal.Round(gross * 0.015m, 2);
            var netAmt = gross - fees;
            var id = $"CARD-{b + 1:D5}";
            var dest = Hit(0.01) ? "" : $"BANK-{ccy}-OPS";
            if (dest.Length == 0) Seed_("settlement.missing_destination_account");
            settlements.Row(gross, ccy, $"SRC-C-{b + 1:D5}", id, "PROCESSOR-A", Entity, Date, A(gross, ccy), A(fees, ccy), A(0, ccy), A(netAmt, ccy), ccy, dest, "SETTLED");
            Bank($"BTX-C-{b + 1:D5}", ccy, netAmt, "CREDIT", id);
        }

        // Balances: recorded = opening + posted ledger movements; 0.2% of accounts recorded wrongly.
        var recordedTotal = Ccys.ToDictionary(c => c, _ => 0m);
        for (var i = 0; i < accounts; i++)
        {
            var ccy = acctCcy[i];
            var recorded = opening[i] + net[i];
            if (Hit(0.002)) { recorded += ccy == "JPY" ? 1000 : 12.34m; Seed_("balance.recorded_wrong"); }
            recordedTotal[ccy] += recorded;
            balances.Row(recorded, ccy, Acct(i), Entity, Date, "DAILY_ROLLFORWARD", A(opening[i], ccy), A(recorded, ccy), ccy, Ts(23, 59, 59));
        }

        // Safeguarding: 3 CLOSING_AVAILABLE accounts per currency plus one CLOSING_LEDGER. JPY is covered at 97% (shortfall);
        // one GBP row has no evidence reference and is rejected (GBP then falls short too); EUR is covered.
        var factor = new Dictionary<string, decimal> { ["GBP"] = 1.02m, ["EUR"] = 1.05m, ["JPY"] = 0.97m };
        var sgRow = 0;
        foreach (var ccy in Ccys)
        {
            var part = decimal.Round(Math.Max(0, recordedTotal[ccy]) * factor[ccy] / 3m, ccy == "JPY" ? 0 : 2);
            for (var k = 1; k <= 3; k++)
            {
                var evidence = ccy == "GBP" && k == 3 ? "" : $"STATEMENT-{ccy}-{k:D3}";
                if (evidence.Length == 0) Seed_("safeguarding.missing_evidence_reference");
                sg.Row(part, ccy, $"SRC-S-{++sgRow:D3}", $"SG-{ccy}-{k:D3}", Entity, Ts(23, 59, 59), "CLOSING_AVAILABLE", A(part, ccy), ccy, evidence, Ts(6, 30, dayOffset: 1));
            }
            sg.Row(part, ccy, $"SRC-S-{++sgRow:D3}", $"SG-{ccy}-L01", Entity, Ts(23, 59, 59), "CLOSING_LEDGER", A(part, ccy), ccy, $"STATEMENT-{ccy}-L01", Ts(6, 30, dayOffset: 1));
        }

        var files = new[] { transfers, dds, cards, settlements, ledger, bank, balances, sg }
            .Select(s => new { s, Id = $"DEL-{s.Source.ToUpperInvariant()}-20261027-01" }).ToList();
        foreach (var f in files) f.s.Dispose();
        return new GenerationSummary(scale, accounts, n,
            files.Select(f => new GeneratedFile(f.s.Source, f.Id, f.s.Path, f.s.Rows, f.s.Totals, new FileInfo(f.s.Path).Length)).ToList(), seeded);
    }
}
