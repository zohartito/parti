// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Parti.Eval.V01;

/// <summary>
/// HTTP handler on the live provider's <see cref="HttpClient"/> that records every request and
/// blocks any request whose host or port differs from the configured endpoint (S20). A blocked
/// request never leaves the process: the handler throws before calling the inner handler, and the
/// live runner fails if <see cref="ForeignRequests"/> is non-empty.
/// </summary>
public sealed class EgressRecorder : DelegatingHandler
{
    private readonly ConcurrentQueue<Uri> _requests = new();
    private readonly ConcurrentQueue<Uri> _foreign = new();

    public EgressRecorder(Uri allowedEndpoint, HttpMessageHandler inner)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        AllowedEndpoint = allowedEndpoint ?? throw new ArgumentNullException(nameof(allowedEndpoint));
        if (!allowedEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The allowed endpoint must be an absolute URI.", nameof(allowedEndpoint));
        }
    }

    /// <summary>The one endpoint (scheme host and port) requests may go to.</summary>
    public Uri AllowedEndpoint { get; }

    /// <summary>Every request URI seen, allowed or not.</summary>
    public IReadOnlyList<Uri> Requests => _requests.ToList();

    /// <summary>Requests that were blocked because they targeted another host or port.</summary>
    public IReadOnlyList<Uri> ForeignRequests => _foreign.ToList();

    /// <summary>Distinct <c>host:port</c> pairs seen.</summary>
    public IReadOnlyList<string> Hosts => _requests.Select(u => $"{u.Host}:{u.Port}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when <paramref name="uri"/> targets the allowed host and port.</summary>
    public bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.Host, AllowedEndpoint.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == AllowedEndpoint.Port;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? new Uri("about:blank");
        _requests.Enqueue(uri);
        if (!IsAllowed(uri))
        {
            _foreign.Enqueue(uri);
            throw new HttpRequestException(
                $"Egress blocked: {uri.Host}:{uri.Port} is not the configured provider endpoint {AllowedEndpoint.Host}:{AllowedEndpoint.Port}.");
        }
        return base.SendAsync(request, cancellationToken);
    }
}
