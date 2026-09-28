# NOTICE — third-party code and attributions

Compilarr is licensed under GPL-3.0 (see `LICENSE`). This file lists code ported or adapted from other projects and software redistributed in the Docker image. Every ported file also carries a header comment naming its source, path and licence. Keep this list current in the same change that adds or removes ported code.

## Ported / adapted code

Ported from Lidarr at commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd` unless noted. `scripts/check-notice.py` (run in CI) fails when a file with a `Ported from` header is missing here.

| Source project | Licence | Upstream path | Where in this repo |
|---|---|---|---|
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/ApiKeyAuthenticationHandler.cs` | `src/Compilarr.Api/Authentication/ApiKeyAuthenticationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/AuthenticationBuilderExtensions.cs` | `src/Compilarr.Api/Authentication/AuthenticationBuilderExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/AuthenticationController.cs` | `src/Compilarr.Api/Authentication/AuthenticationController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/BypassableDenyAnonymousAuthorizationRequirement.cs` | `src/Compilarr.Api/Authentication/BypassableDenyAnonymousAuthorizationRequirement.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Frontend/InitializeJsonController.cs` | `src/Compilarr.Api/Authentication/InitializeController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/LoginResource.cs` | `src/Compilarr.Api/Authentication/LoginResource.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/NoAuthenticationHandler.cs` | `src/Compilarr.Api/Authentication/NoAuthenticationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/UiAuthorizationHandler.cs` | `src/Compilarr.Api/Authentication/UiAuthorizationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/UiAuthorizationPolicyProvider.cs` | `src/Compilarr.Api/Authentication/UiAuthorizationPolicyProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Common/Extensions/IpAddressExtensions.cs` | `src/Compilarr.Api/Extensions/IpAddressExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Extensions/RequestExtensions.cs` | `src/Compilarr.Api/Extensions/RequestExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Middleware/UrlBaseMiddleware.cs` | `src/Compilarr.Api/Middleware/UrlBaseMiddleware.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Ping/PingController.cs` | `src/Compilarr.Api/Ping/PingController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Common/Instrumentation/CleanseLogMessage.cs` | `src/Compilarr.Core/Logging/CleanseLogMessage.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/Health/HealthController.cs` | `src/Compilarr.Api/Health/HealthController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/Health/HealthResource.cs` | `src/Compilarr.Api/Health/HealthResource.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/HealthCheck.cs` | `src/Compilarr.Core/HealthCheck/HealthCheck.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/HealthCheckService.cs` | `src/Compilarr.Core/HealthCheck/HealthCheckService.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/IProvideHealthCheck.cs` | `src/Compilarr.Core/HealthCheck/IHealthCheck.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Frontend/Mappers/UrlBaseReplacementResourceMapperBase.cs` | `src/Compilarr.Api/Frontend/IndexHtmlProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/System/SystemController.cs` | `src/Compilarr.Api/SystemInfo/SystemController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/System/SystemResource.cs` | `src/Compilarr.Api/SystemInfo/SystemResource.cs` |

Planned sources (see ADR-0002): Lidarr, Prowlarr, Sonarr (GPL-3.0); SoulSync, spotDL (MIT). Sockseek and slskd are AGPL-3.0 and are **not** copied.

## Bundled third-party programs

These are redistributed unmodified as separate programs inside the Docker image, under their own licences.

| Software | Version | Licence | Notes |
|---|---|---|---|
| slskd | 0.26.0 | AGPL-3.0 with additional terms | Unmodified binary, run as a separate process; licence, additional terms and a link to its source (https://github.com/slskd/slskd/tree/0.26.0) are shipped in the image |
| ffmpeg / ffprobe (static builds) | 9.0.2 | GPL (build-dependent) | Unmodified binaries, via [mwader/static-ffmpeg](https://github.com/mwader/static-ffmpeg) |
| Chromaprint `fpcalc` | 1.6.1 | LGPL-2.1+ / MIT components | Unmodified binary, used for AcoustID fingerprinting |
| Deno | 2.9.7 | MIT | Unmodified binary, required by yt-dlp's JavaScript challenge solver |
| s6-overlay | 3.2.3.2 | ISC | Init system, from https://github.com/just-containers/s6-overlay |

yt-dlp (Unlicense) joins the image in Phase 4 (YouTube source).
