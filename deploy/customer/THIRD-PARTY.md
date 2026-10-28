# Third-party software

Policy (ADR 0004 and issue #52): only permissive licences (MIT, Apache-2.0, BSD, ISC, PostgreSQL) may ship in the on-premises product.
Copyleft or source-available licences (GPL, AGPL, LGPL, SSPL, BUSL, EPL, MPL in shipped code) and packages with an unknown licence need an explicit
decision recorded here. Licences were read from each package's `.nuspec` (`license` / `licenseUrl`) and from `package.json`, not from memory.

Reviewed on 2026-10-28 from the real dependency graph:

- .NET: `dotnet list backend/Proofly.sln package --include-transitive` (net10.0, central versions in `backend/Directory.Packages.props`), licence from `~/.nuget/packages/<id>/<version>/<id>.nuspec`.
- Web: `package-lock.json` of `apps/web` (`npm ls --all`), licence from each `node_modules/<pkg>/package.json`.

Re-run both before every release and update this file in the same PR (a licence or version change is a release blocker until reviewed).

## Summary

| Area | Packages | MIT | Apache-2.0 | PostgreSQL / BSD / ISC | Needs a decision |
|---|---|---|---|---|---|
| .NET shipped (Api, Worker and their libraries) | 62 | 41 | 17 | 1 | 3 (FLAG 1) |
| .NET test-only (never shipped) | 15 | 7 | 8 | 0 | 0 |
| npm runtime closure (bundled into the web build) | 98 | 95 | 0 | 3 | 0 |
| npm build tooling (not shipped) | 45 | - | - | - | 0 blocking (FLAG 3, tool only) |

## Findings that need an owner decision

### FLAG 1: JsonSchema.Net, Json.More.Net and JsonPointer.Net use the Open Source Maintenance Fee EULA (shipped; conflicts with the MIT/Apache-only policy)

- Packages: `JsonSchema.Net 9.5.0` (direct, `Proofly.Api`), `Json.More.Net 3.0.1` and `JsonPointer.Net 7.0.3` (transitive). Their `.nuspec` says `<license type="file">OSMFEULA.txt</license>`, not an SPDX expression.
- The source is MIT, but the pre-built NuGet binaries come with the "Open Source Maintenance Fee Agreement": users of the binary release in revenue-generating activity with annual gross revenue of US$10,000 or more owe the project a fee, unless they pay separate support or maintenance fees to the maintainers. The agreement states that the fee is not a licence fee and that self-compiled binaries are free, but it is a non-standard contractual obligation attached to a library that is shipped to customers and used on every request (contract validation in `Proofly.Api/Platform/Contract`).
- Not acceptable under the permissive-only policy without a decision. Options: (a) pay the fee or buy support and record the agreement here; (b) build the three libraries from their MIT source in CI and consume the self-built packages; (c) replace JSON Schema validation with a library under a plain MIT or Apache licence; (d) pin a release made before the EULA (verify that version's licence first).
- Owner: product and legal. Not changed in code by issue #52.

### FLAG 2: prerelease packages in the shipped graph

| Package | Version | Licence | Why |
|---|---|---|---|
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.20.1-beta.1 | Apache-2.0 | the Prometheus exporter has no stable release (`/metrics`, off by default) |
| OpenTelemetry.Exporter.Prometheus.HttpListener | 1.20.1-beta.1 | Apache-2.0 | same, for the worker listener |

Accepted for the pilot because metrics are off by default and only reachable from allow-listed addresses; revisit when a stable release exists. There are no other prerelease packages in the .NET or npm graph.

### FLAG 3: MPL-2.0 in the web build tooling (not shipped)

`lightningcss 1.33.0` and its 11 platform binaries (`lightningcss-<os>-<arch>`) are MPL-2.0 (file-level copyleft). They are a dependency of Vite, used only at build time to process CSS; no MPL code is part of the files in `apps/web/dist`. If a build image containing `node_modules` were ever distributed, the MPL-2.0 notice and source offer for those files would apply. Not a blocker.

### Other observations

- `Npgsql 10.0.0` is under the PostgreSQL licence (permissive, OSI approved, MIT-like). Accepted.
- `xunit.abstractions 2.0.3` (tests only) points to the xunit licence URL (Apache-2.0); it is not shipped.
- No GPL, AGPL, LGPL, SSPL, BUSL or unknown-licence package exists in either graph.
- The .NET 10 runtime and ASP.NET Core shared framework are MIT (Microsoft). PostgreSQL itself is provided by the customer (PostgreSQL licence) and is not redistributed.

## .NET packages shipped with the API and worker

| Package | Version | Licence | Used by | Direct |
|---|---|---|---|---|
| Humanizer.Core | 3.0.10 | MIT | Api |  |
| Json.More.Net | 3.0.1 | MIT source + Open Source Maintenance Fee EULA (FLAG 1) | Api |  |
| JsonPointer.Net | 7.0.3 | MIT source + Open Source Maintenance Fee EULA (FLAG 1) | Api |  |
| JsonSchema.Net | 9.5.0 | MIT source + Open Source Maintenance Fee EULA (FLAG 1) | Api | yes |
| Microsoft.Extensions.Configuration | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.0 | MIT | Data, Worker | yes |
| Microsoft.Extensions.Configuration.Binder | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.Json | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT | Data, Worker | yes |
| Microsoft.Extensions.DependencyModel | 10.0.0 | MIT | Api |  |
| Microsoft.Extensions.Diagnostics | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.FileProviders.Physical | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Hosting | 10.0.0 | MIT | Data, Worker | yes |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Hosting.Systemd | 10.0.0 | MIT | Api, Worker | yes |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.0 | MIT | Api, Worker | yes |
| Microsoft.Extensions.Logging | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.Configuration | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.Console | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.Debug | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.EventLog | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Logging.EventSource | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Options | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.Extensions.Primitives | 10.0.0 | MIT | Data, Worker |  |
| Microsoft.IdentityModel.Abstractions | 8.12.0 | MIT | Api |  |
| Microsoft.IdentityModel.JsonWebTokens | 8.12.0 | MIT | Api | yes |
| Microsoft.IdentityModel.Logging | 8.12.0 | MIT | Api |  |
| Microsoft.IdentityModel.Protocols | 8.12.0 | MIT | Api |  |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 8.12.0 | MIT | Api | yes |
| Microsoft.IdentityModel.Tokens | 8.12.0 | MIT | Api |  |
| Npgsql | 10.0.0 | PostgreSQL | Api, Data, Worker | yes |
| OpenTelemetry | 1.20.1 | Apache-2.0 | Api, Worker |  |
| OpenTelemetry.Api | 1.20.1 | Apache-2.0 | Api, Worker |  |
| OpenTelemetry.Api.ProviderBuilderExtensions | 1.20.1 | Apache-2.0 | Api, Worker |  |
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.20.1-beta.1 | Apache-2.0 | Api | yes |
| OpenTelemetry.Exporter.Prometheus.HttpListener | 1.20.1-beta.1 | Apache-2.0 | Worker | yes |
| OpenTelemetry.Extensions.Hosting | 1.20.1 | Apache-2.0 | Api, Worker | yes |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | Apache-2.0 | Api | yes |
| Serilog | 4.3.0 | Apache-2.0 | Api, Worker |  |
| Serilog.AspNetCore | 10.0.0 | Apache-2.0 | Api | yes |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | Api, Worker | yes |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | Api, Worker |  |
| Serilog.Formatting.Compact | 3.0.0 | Apache-2.0 | Api, Worker | yes |
| Serilog.Settings.Configuration | 10.0.0 | Apache-2.0 | Api |  |
| Serilog.Sinks.Console | 6.0.0 | Apache-2.0 | Worker | yes |
| Serilog.Sinks.Console | 6.1.1 | Apache-2.0 | Api |  |
| Serilog.Sinks.Debug | 3.0.0 | Apache-2.0 | Api |  |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Api |  |
| System.Diagnostics.EventLog | 10.0.0 | MIT | Data, Worker |  |
| System.IdentityModel.Tokens.Jwt | 8.12.0 | MIT | Api |  |
| System.ServiceProcess.ServiceController | 10.0.0 | MIT | Api, Worker |  |
| YamlDotNet | 18.1.0 | MIT | Api | yes |

## .NET packages used only by tests (not shipped)

| Package | Version | Licence |
|---|---|---|
| Microsoft.AspNetCore.Mvc.Testing | 10.0.0 | MIT |
| Microsoft.AspNetCore.TestHost | 10.0.0 | MIT |
| Microsoft.CodeCoverage | 17.14.1 | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.TestPlatform.ObjectModel | 17.14.1 | MIT |
| Microsoft.TestPlatform.TestHost | 17.14.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Apache-2.0 (xunit licence URL) |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.assert | 2.9.3 | Apache-2.0 |
| xunit.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.2 | Apache-2.0 |

## npm packages bundled into the web build (runtime closure of `dependencies`)

Direct: `vue`, `vue-router`, `ant-design-vue`, `@ant-design/icons-vue`.

| Package | Version | Licence |
|---|---|---|
| @ant-design/colors | 6.0.0 | MIT |
| @ant-design/icons-svg | 4.6.0 | MIT |
| @ant-design/icons-vue | 7.0.1 | MIT |
| @babel/helper-string-parser | 7.29.11 | MIT |
| @babel/helper-string-parser | 8.0.8 | MIT |
| @babel/helper-validator-identifier | 7.29.7 | MIT |
| @babel/helper-validator-identifier | 8.0.6 | MIT |
| @babel/parser | 7.29.11 | MIT |
| @babel/parser | 8.0.8 | MIT |
| @babel/runtime | 7.29.10 | MIT |
| @babel/types | 7.29.11 | MIT |
| @babel/types | 8.0.8 | MIT |
| @ctrl/tinycolor | 3.6.1 | MIT |
| @emotion/hash | 0.9.2 | MIT |
| @emotion/unitless | 0.8.1 | MIT |
| @jridgewell/gen-mapping | 0.3.13 | MIT |
| @jridgewell/remapping | 2.3.5 | MIT |
| @jridgewell/resolve-uri | 3.1.2 | MIT |
| @jridgewell/sourcemap-codec | 1.6.0 | MIT |
| @jridgewell/trace-mapping | 0.3.31 | MIT |
| @rolldown/pluginutils | 1.0.1 | MIT |
| @simonwep/pickr | 1.8.2 | MIT |
| @types/estree | 1.0.9 | MIT |
| @vue-macros/common | 3.1.4 | MIT |
| @vue/compiler-core | 3.5.44 | MIT |
| @vue/compiler-dom | 3.5.44 | MIT |
| @vue/compiler-sfc | 3.5.44 | MIT |
| @vue/compiler-ssr | 3.5.44 | MIT |
| @vue/devtools-api | 8.2.1 | MIT |
| @vue/devtools-kit | 8.2.1 | MIT |
| @vue/devtools-shared | 8.2.1 | MIT |
| @vue/reactivity | 3.5.44 | MIT |
| @vue/runtime-core | 3.5.44 | MIT |
| @vue/runtime-dom | 3.5.44 | MIT |
| @vue/server-renderer | 3.5.44 | MIT |
| @vue/shared | 3.5.44 | MIT |
| acorn | 8.19.0 | MIT |
| ant-design-vue | 4.2.6 | MIT |
| array-tree-filter | 2.1.0 | MIT |
| ast-kit | 2.2.0 | MIT |
| ast-kit | 3.0.0 | MIT |
| ast-walker-scope | 0.10.0 | MIT |
| async-validator | 4.2.5 | MIT |
| birpc | 2.9.0 | MIT |
| chokidar | 5.0.0 | MIT |
| compute-scroll-into-view | 1.0.20 | MIT |
| confbox | 0.1.8 | MIT |
| confbox | 0.3.1 | MIT |
| core-js | 3.51.0 | MIT |
| csstype | 3.2.3 | MIT |
| dayjs | 1.11.23 | MIT |
| dom-align | 1.12.4 | MIT |
| dom-scroll-into-view | 2.0.1 | MIT |
| entities | 7.0.1 | BSD-2-Clause |
| estree-walker | 2.0.2 | MIT |
| estree-walker | 3.0.3 | MIT |
| exsolve | 1.2.0 | MIT |
| fdir | 6.5.0 | MIT |
| hookable | 5.5.3 | MIT |
| is-plain-object | 3.0.1 | MIT |
| js-tokens | 4.0.0 | MIT |
| local-pkg | 1.2.1 | MIT |
| lodash | 4.18.1 | MIT |
| lodash-es | 4.18.1 | MIT |
| loose-envify | 1.4.0 | MIT |
| magic-string | 0.30.21 | MIT |
| magic-string | 1.4.5 | MIT |
| magic-string-ast | 1.0.3 | MIT |
| mlly | 1.8.2 | MIT |
| muggle-string | 0.4.1 | MIT |
| nanoid | 3.3.20 | MIT |
| nanopop | 2.4.2 | MIT |
| nostics | 1.3.0 | MIT |
| pathe | 2.0.3 | MIT |
| perfect-debounce | 2.1.0 | MIT |
| picocolors | 1.1.1 | ISC |
| picomatch | 4.0.7 | MIT |
| pkg-types | 1.3.1 | MIT |
| pkg-types | 2.4.0 | MIT |
| postcss | 8.5.32 | MIT |
| quansync | 0.2.11 | MIT |
| readdirp | 5.1.1 | MIT |
| resize-observer-polyfill | 1.5.1 | MIT |
| scroll-into-view-if-needed | 2.2.31 | MIT |
| scule | 1.3.0 | MIT |
| shallow-equal | 1.2.1 | MIT |
| source-map-js | 1.2.2 | BSD-3-Clause |
| stylis | 4.4.0 | MIT |
| throttle-debounce | 5.0.2 | MIT |
| tinyglobby | 0.2.17 | MIT |
| ufo | 1.6.4 | MIT |
| unplugin | 3.7.0 | MIT |
| unplugin-utils | 0.3.2 | MIT |
| vue | 3.5.44 | MIT |
| vue-router | 5.4.0 | MIT |
| vue-types | 3.0.2 | MIT |
| warning | 4.0.3 | MIT |
| webpack-virtual-modules | 0.6.2 | MIT |

## npm build tooling (devDependencies and their dependencies, not shipped)

45 packages (Vite, Rolldown, TypeScript, vue-tsc, the Vue compiler plugin and their platform binaries). Licences: MIT except `lightningcss*` (MPL-2.0, see FLAG 3), `typescript` and `detect-libc` (Apache-2.0). The full list is `apps/web/package-lock.json`.
