# Performance targets (oMVP)

## Status

Accepted for the oMVP pilot (decision #49, product-owner guidance: simplest safe choice). These are **targets to verify**, not claims. They are set with wide margin over the single-machine measurements in [performance-spike.md](performance-spike.md) (Apple M4, synthetic data, loopback) and must be re-measured on the customer's reference environment before any commitment.

## Reference sizing

One application host (api + worker) with 4 vCPU and 16 GB RAM, PostgreSQL 14+ on the same or a separate host with SSD storage, at the upper end of the oMVP envelope: 100,000 transactions and 100,000 client accounts per business date.

## Targets

| Area | Target | Measured on the spike machine (for reference) |
|---|---|---|
| Ingest of all deliveries of a business date (about 370,000 source rows) | at most 10 minutes | 12 s |
| Run processing (controls and persistence) | at most 15 minutes | 27.5 s |
| Reprocess with unchanged evidence | at most 15 minutes, identical result hash | 26.7 s |
| Evidence pack generation | at most 10 minutes | not measured |
| Worker peak memory at the upper envelope | at most 4 GB | about 2 GB |
| API read latency, p95, list and detail operations, 4 concurrent users | at most 1.5 s | 4 ms to 1.6 s (see spike doc) |
| API read latency, p95, aggregate views (control room, safeguarding, summary) | at most 2 s | 0.3 s to 1.6 s |
| Database growth per processed run version at the upper envelope | at most 2 GB | about 0.7 GB |

## Deadline

The T+1 control results for a business date are available by **09:00 local time** provided the last mandatory delivery arrived by **07:30**. The processing budget therefore leaves at least 90 minutes of margin.

## Rules

- Targets are checked by re-running `backend/tools/Proofly.Benchmarks` on the customer reference environment during the pilot acceptance run (#53), and the results are recorded in the pilot report.
- No throughput or latency claim is published beyond what was measured on the target environment.
- A target miss is a defect or a sizing recommendation, not a reason to relax a control.
