# Third-party packages added for packaging and observability

Policy: MIT or Apache-2.0 only. Licences taken from the package metadata on nuget.org.

| Package | Version | Licence | Used by |
|---|---|---|---|
| Serilog.AspNetCore | 10.0.0 | Apache-2.0 | Api (JSON logs) |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | Worker |
| Serilog.Formatting.Compact | 3.0.0 | Apache-2.0 | Api, Worker |
| Serilog.Sinks.Console | 6.0.0 | Apache-2.0 | Worker (Api via Serilog.AspNetCore) |
| OpenTelemetry.Extensions.Hosting | 1.20.1 | Apache-2.0 | Api, Worker |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | Apache-2.0 | Api |
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.20.1-beta.1 | Apache-2.0 | Api (`/metrics`; prerelease, the exporter has no stable release) |
| OpenTelemetry.Exporter.Prometheus.HttpListener | 1.20.1-beta.1 | Apache-2.0 | Worker (prerelease) |
| Microsoft.Extensions.Hosting.Systemd | 10.0.0 | MIT | Api, Worker |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.0 | MIT | Api, Worker |

Transitive packages (Serilog.Settings.Configuration, Serilog.Sinks.*, OpenTelemetry core/API) carry the same Apache-2.0 licence. Re-verify with `dotnet list package --include-transitive` and the nuspec `license` element before each release.
