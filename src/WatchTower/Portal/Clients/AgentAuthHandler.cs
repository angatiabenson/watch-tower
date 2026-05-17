using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using WatchTower.Configuration;

namespace WatchTower.Portal.Clients;

public class AgentAuthHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<PortalOptions> _options;

    public AgentAuthHandler(IOptionsMonitor<PortalOptions> options)
    {
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.AgentToken);
        request.Headers.TryAddWithoutValidation("X-Agent-Id", opts.AgentId);
        return base.SendAsync(request, cancellationToken);
    }
}
