using System.Net.Http.Headers;

using Microsoft.Extensions.Options;

using WatchTower.Configuration;

namespace WatchTower.Portal.Clients;

/// <summary>
/// Adds the agent credentials (bearer token and <c>X-Agent-Id</c>) to portal requests, but only
/// to requests below the configured <see cref="PortalOptions.BaseUrl"/>.
/// </summary>
/// <remarks>
/// Redirects are not followed (see <see cref="PortalTls.CreateHandler"/>), so every request
/// passes through here with the address the client built. A request for any other address is
/// refused rather than sent without credentials: it can only come from a bug, and the token
/// must never travel to a host the operator did not configure.
/// </remarks>
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
        if (!IsBelowBaseUrl(request.RequestUri, opts.BaseUrl))
        {
            throw new InvalidOperationException(
                $"Refusing to send the agent credentials to '{request.RequestUri?.GetLeftPart(UriPartial.Path)}': " +
                $"it is not below {PortalOptions.SectionName}:BaseUrl '{opts.BaseUrl}'.");
        }

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.AgentToken);
        request.Headers.TryAddWithoutValidation("X-Agent-Id", opts.AgentId);
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="requestUri"/> has the scheme, host and port of
    /// <paramref name="baseUrl"/> and a path at or below its path.
    /// </summary>
    internal static bool IsBelowBaseUrl(Uri? requestUri, string baseUrl)
    {
        if (requestUri is not { IsAbsoluteUri: true } ||
            !Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var root))
        {
            return false;
        }

        if (!string.Equals(requestUri.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.IdnHost, root.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != root.Port)
        {
            return false;
        }

        var rootPath = root.AbsolutePath.TrimEnd('/') + "/";
        var path = requestUri.AbsolutePath.TrimEnd('/') + "/";
        return path.StartsWith(rootPath, StringComparison.Ordinal);
    }
}