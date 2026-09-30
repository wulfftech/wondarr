# NOTICE — third-party code and attributions

Wondarr is licensed under GPL-3.0 (see `LICENSE`). This file lists code ported or adapted from other projects and software redistributed in the Docker image. Every ported file also carries a header comment naming its source, path and licence. Keep this list current in the same change that adds or removes ported code.

## Ported / adapted code

Ported from Lidarr at commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd` unless noted. `scripts/check-notice.py` (run in CI) fails when a file with a `Ported from` header is missing here.

| Source project | Licence | Upstream path | Where in this repo |
|---|---|---|---|
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/ApiKeyAuthenticationHandler.cs` | `src/Wondarr.Api/Authentication/ApiKeyAuthenticationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/AuthenticationBuilderExtensions.cs` | `src/Wondarr.Api/Authentication/AuthenticationBuilderExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/AuthenticationController.cs` | `src/Wondarr.Api/Authentication/AuthenticationController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/BypassableDenyAnonymousAuthorizationRequirement.cs` | `src/Wondarr.Api/Authentication/BypassableDenyAnonymousAuthorizationRequirement.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Frontend/InitializeJsonController.cs` | `src/Wondarr.Api/Authentication/InitializeController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/LoginResource.cs` | `src/Wondarr.Api/Authentication/LoginResource.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/NoAuthenticationHandler.cs` | `src/Wondarr.Api/Authentication/NoAuthenticationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/UiAuthorizationHandler.cs` | `src/Wondarr.Api/Authentication/UiAuthorizationHandler.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Authentication/UiAuthorizationPolicyProvider.cs` | `src/Wondarr.Api/Authentication/UiAuthorizationPolicyProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Common/Extensions/IpAddressExtensions.cs` | `src/Wondarr.Api/Extensions/IpAddressExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Extensions/RequestExtensions.cs` | `src/Wondarr.Api/Extensions/RequestExtensions.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Middleware/UrlBaseMiddleware.cs` | `src/Wondarr.Api/Middleware/UrlBaseMiddleware.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Ping/PingController.cs` | `src/Wondarr.Api/Ping/PingController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Common/Instrumentation/CleanseLogMessage.cs` | `src/Wondarr.Core/Logging/CleanseLogMessage.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/Health/HealthController.cs` | `src/Wondarr.Api/Health/HealthController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/Health/HealthResource.cs` | `src/Wondarr.Api/Health/HealthResource.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/HealthCheck.cs` | `src/Wondarr.Core/HealthCheck/HealthCheck.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/HealthCheckService.cs` | `src/Wondarr.Core/HealthCheck/HealthCheckService.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/HealthCheck/IProvideHealthCheck.cs` | `src/Wondarr.Core/HealthCheck/IHealthCheck.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/Frontend/Mappers/UrlBaseReplacementResourceMapperBase.cs` | `src/Wondarr.Api/Frontend/IndexHtmlProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/System/SystemController.cs` | `src/Wondarr.Api/SystemInfo/SystemController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/System/SystemResource.cs` | `src/Wondarr.Api/SystemInfo/SystemResource.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Http/SignalR/SignalRMessage.cs`, `src/Lidarr.Http/ResourceChangeMessage.cs` | `src/Wondarr.Api/SignalR/SignalRMessage.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/Lidarr.Api.V1/System/Tasks/TaskController.cs` | `src/Wondarr.Api/SystemInfo/TaskController.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Organizer/FileNameBuilder.cs` | `src/Wondarr.Core/Organizer/NamingTokens.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Webhook/WebhookPayload.cs`, `WebhookGrabPayload.cs`, `WebhookImportPayload.cs`, `WebhookDownloadFailurePayload.cs`, `WebhookHealthPayload.cs` | `src/Wondarr.Core/Notifications/Webhook/WebhookPayloads.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Webhook/Webhook.cs`, `WebhookBase.cs`, `WebhookProxy.cs`, `WebhookEventType.cs` | `src/Wondarr.Core/Notifications/Webhook/WebhookProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Webhook/WebhookSettings.cs`, `WebhookMethod.cs` | `src/Wondarr.Core/Notifications/Webhook/WebhookSettings.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Apprise/AppriseNotificationType.cs` | `src/Wondarr.Core/Notifications/Apprise/AppriseNotificationType.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Apprise/ApprisePayload.cs` | `src/Wondarr.Core/Notifications/Apprise/ApprisePayload.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Apprise/Apprise.cs`, `AppriseProxy.cs` | `src/Wondarr.Core/Notifications/Apprise/AppriseProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Apprise/AppriseSettings.cs` | `src/Wondarr.Core/Notifications/Apprise/AppriseSettings.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Discord/DiscordColors.cs` | `src/Wondarr.Core/Notifications/Discord/DiscordColors.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Discord/Payloads/DiscordPayload.cs`, `Embed.cs`, `DiscordField.cs`, `DiscordAuthor.cs`, `DiscordImage.cs` | `src/Wondarr.Core/Notifications/Discord/DiscordPayloads.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Discord/Discord.cs`, `DiscordProxy.cs` | `src/Wondarr.Core/Notifications/Discord/DiscordProvider.cs` |
| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `src/NzbDrone.Core/Notifications/Discord/DiscordSettings.cs` | `src/Wondarr.Core/Notifications/Discord/DiscordSettings.cs` |

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
