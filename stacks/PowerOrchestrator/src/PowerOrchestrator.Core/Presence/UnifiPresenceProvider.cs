using Microsoft.Extensions.Logging;
using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Model;
using UnifiSharp.Api;
using UnifiSharp.Api.Models;

namespace PowerOrchestrator.Core.Presence;

/// <summary>
/// Presence via UniFi: a tracked MAC (e.g. the owner's phone) appearing in a site's connected
/// clients means "someone home". UniFi has no get-by-MAC endpoint, so we list each site's
/// clients and match locally (the same call UnifiDiscovery makes). Read-only.
/// </summary>
public sealed class UnifiPresenceProvider(
    UnifiApiClient client,
    IReadOnlyList<string> presenceMacs,
    ILogger<UnifiPresenceProvider> logger) : IPresenceSource
{
    private readonly HashSet<string> _tracked =
        new(presenceMacs.Select(OrchestratorOptions.NormalizeMac), StringComparer.OrdinalIgnoreCase);

    public async Task<PresenceState> GetAsync(CancellationToken ct = default)
    {
        if (_tracked.Count == 0)
        {
            logger.LogDebug("No presence MACs configured; presence is always 'away'.");
            return PresenceState.Empty;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sites = (await client.V1.Sites.GetAsync(cancellationToken: ct).ConfigureAwait(false))?.Data ?? [];

        foreach (var site in sites)
        {
            if (site.Id is not Guid id) continue;
            var clientsBuilder = client.V1.Sites[id].Clients;

            // The clients endpoint pages (default 25); a busy network easily exceeds one page, so
            // walk every page or the tracked device hides past page 1. Early-exit once all found.
            const int pageSize = 200;
            var offset = 0;
            while (true)
            {
                var page = await clientsBuilder.GetAsync(rc =>
                {
                    rc.QueryParameters.Limit = pageSize;
                    rc.QueryParameters.Offset = offset;
                }, ct).ConfigureAwait(false);

                var data = page?.Data ?? [];
                foreach (var c in data)
                {
                    if (MacOf(c) is { } mac)
                    {
                        var norm = OrchestratorOptions.NormalizeMac(mac);
                        if (_tracked.Contains(norm)) present.Add(norm);
                    }
                }

                if (present.Count == _tracked.Count)
                {
                    logger.LogDebug("Presence: all {Tracked} tracked device(s) online", _tracked.Count);
                    return new PresenceState(present.Count, present.ToList());
                }

                offset += data.Count;
                var total = page?.TotalCount ?? data.Count;
                if (data.Count == 0 || offset >= total) break;
            }
        }

        logger.LogDebug("Presence: {Count}/{Tracked} tracked device(s) online", present.Count, _tracked.Count);
        return new PresenceState(present.Count, present.ToList());
    }

    // MAC lives on the wired/wireless subtypes of the discriminated ClientOverview, not the base.
    private static string? MacOf(ClientOverview c) => c switch
    {
        WirelessClientOverview w => w.MacAddress,
        WiredClientOverview w => w.MacAddress,
        _ => null,
    };
}
