// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Parti.Core.Ai;

/// <summary>
/// Shared bounded-I/O helpers for HTTP model providers (Ollama, OpenAI-compatible). Every helper
/// fails closed with a <see cref="BoundedHttpException"/> when a budget is exceeded; none of them
/// truncates response material that a parser will consume. <c>ReadAsStringAsync</c> is banned on
/// provider paths because it buffers an unbounded body.
/// <para>
/// The Anthropic provider keeps its own audited copy of these helpers; it is not refactored onto
/// this class (see docs/SECURITY-P3-REMEDIATION.md).
/// </para>
/// </summary>
public static class BoundedHttp
{
    /// <summary>
    /// Default cap for a non-success response body that is only used as an error diagnostic.
    /// </summary>
    public const int DefaultErrorDiagnosticBytes = 16_384;

    /// <summary>
    /// Suffix appended to an error diagnostic that was cut at its budget. Diagnostics are the only
    /// response material that is truncated rather than rejected, because they are never parsed.
    /// </summary>
    public const string DiagnosticTruncatedMarker = " [diagnostic truncated]";

    /// <summary>
    /// Bound on establishing the connection to a provider. Without it an unreachable host (an
    /// offline remote machine) holds the chat in "Streaming..." for the OS TCP timeout (~75 s).
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The error text shown when a provider connection could not be established in time.
    /// </summary>
    public const string ConnectTimeoutMessage =
        "Could not connect to the model server (connection timed out); check that it is running and reachable.";

    /// <summary>
    /// Creates the <see cref="HttpClient"/> a provider owns: no whole-request timeout (the provider's
    /// own cancellation sources bound the header wait and each idle read, so a loading model is not
    /// aborted) but a bounded connect timeout.
    /// </summary>
    public static HttpClient CreateProviderClient()
    {
        return new HttpClient(CreateProviderHandler(DefaultConnectTimeout)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Creates the message handler behind <see cref="CreateProviderClient"/>. The connect step
    /// (TCP, via <paramref name="connect"/>; a plain socket connect when null) is bounded by
    /// <paramref name="connectTimeout"/>; running out throws a
    /// <see cref="ProviderConnectTimeoutException"/>, so the failure is recognisable and never
    /// confused with an <see cref="HttpClient.Timeout"/> or a read timeout (which .NET reports
    /// with the same exception shape as its own ConnectTimeout).
    /// </summary>
    public static SocketsHttpHandler CreateProviderHandler(
        TimeSpan connectTimeout,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        var connectAsync = connect ?? ConnectSocketAsync;
        return new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(connectTimeout);
                try
                {
                    return await connectAsync(context.DnsEndPoint, bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
                {
                    throw new ProviderConnectTimeoutException(ConnectTimeoutMessage);
                }
            },
        };
    }

    private static async ValueTask<Stream> ConnectSocketAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) is the provider handler's
    /// <see cref="ProviderConnectTimeoutException"/>. Caller cancellations, read timeouts and an
    /// injected client's <see cref="HttpClient.Timeout"/> are never reported as connect failures.
    /// </summary>
    public static bool IsConnectTimeout(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ProviderConnectTimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a configured provider endpoint: absolute, <c>http</c> or <c>https</c>, a non-empty
    /// host, no user-info, no query string and no fragment.
    /// </summary>
    public static bool TryValidateEndpoint(Uri? endpoint, out string error)
    {
        if (endpoint is null)
        {
            error = "Endpoint is required.";
            return false;
        }

        if (!endpoint.IsAbsoluteUri)
        {
            error = "Endpoint must be an absolute URL.";
            return false;
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Endpoint must use http or https.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            error = "Endpoint must name a host.";
            return false;
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            error = "Endpoint must not contain user credentials.";
            return false;
        }

        if (!string.IsNullOrEmpty(endpoint.Query))
        {
            error = "Endpoint must not contain a query string.";
            return false;
        }

        if (!string.IsNullOrEmpty(endpoint.Fragment))
        {
            error = "Endpoint must not contain a fragment.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// True when both endpoints have the same scheme, host, port and path (ignoring a trailing
    /// slash and host case). Used to refuse a request whose base URL differs from the configured
    /// endpoint, so credentials only ever reach the configured host.
    /// </summary>
    public static bool IsSameEndpoint(Uri first, Uri second)
    {
        if (first is null || second is null || !first.IsAbsoluteUri || !second.IsAbsoluteUri)
        {
            return false;
        }

        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
            && first.Port == second.Port
            && string.Equals(
                first.AbsolutePath.TrimEnd('/'),
                second.AbsolutePath.TrimEnd('/'),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the endpoint's host is a loopback address or <c>localhost</c>.
    /// </summary>
    public static bool IsLoopback(Uri endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri)
        {
            return false;
        }

        if (endpoint.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(endpoint.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Joins a validated base URL and an endpoint path (which starts with '/').
    /// </summary>
    public static Uri Combine(Uri baseUrl, string path)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }

        var suffix = string.IsNullOrEmpty(path) ? string.Empty : (path[0] == '/' ? path : "/" + path);
        return new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + suffix);
    }

    /// <summary>
    /// Reads a whole response body as UTF-8, failing closed once more than
    /// <paramref name="maxBytes"/> bytes arrive (or are declared by Content-Length).
    /// </summary>
    public static async Task<string> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        using var bounded = await OpenBoundedStreamAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true));
        var body = new StringBuilder();
        var buffer = new char[8_192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return body.ToString();
            }

            body.Append(buffer, 0, read);
        }
    }

    /// <summary>
    /// Opens the response stream behind a <see cref="BoundedReadStream"/>. Fails immediately when the
    /// declared Content-Length already exceeds the budget.
    /// </summary>
    public static async Task<Stream> OpenBoundedStreamAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (content.Headers.ContentLength is long declared && declared > maxBytes)
        {
            throw new BoundedHttpException($"Provider response exceeds the {maxBytes}-byte transport budget.");
        }

        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new BoundedReadStream(source, maxBytes);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of a non-success response for use as a diagnostic.
    /// Anything beyond the cap is discarded and the text is marked with
    /// <see cref="DiagnosticTruncatedMarker"/>. Invalid UTF-8 is replaced, never thrown.
    /// </summary>
    public static async Task<string> ReadErrorDiagnosticAsync(
        HttpContent? content,
        int maxBytes = DefaultErrorDiagnosticBytes,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var declared = content.Headers.ContentLength;
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = declared is long length && length > maxBytes;
        if (!truncated && total == buffer.Length)
        {
            var probe = new byte[1];
            truncated = await stream.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0;
        }

        var body = Encoding.UTF8.GetString(buffer, 0, total);
        return truncated ? body + DiagnosticTruncatedMarker : body;
    }
}

/// <summary>
/// Thrown when provider response material exceeds a local budget. Providers convert it into an
/// error <see cref="AIReturn"/>; it never escapes a provider call.
/// </summary>
public sealed class BoundedHttpException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception with a budget message.
    /// </summary>
    public BoundedHttpException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Counts bytes accepted from an HTTP response and fails before response material can grow beyond
/// the budget. When the budget is reached it asks the inner stream for a single byte to tell EOF
/// from an over-limit body; that byte is never returned to a parser.
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _readBytes;

    /// <summary>
    /// Wraps <paramref name="inner"/> with a <paramref name="maxBytes"/> budget. Disposing this
    /// stream disposes the inner stream.
    /// </summary>
    public BoundedReadStream(Stream inner, long maxBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    /// <summary>
    /// Bytes accepted so far.
    /// </summary>
    public long BytesRead => _readBytes;

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, AllowedCount(count));
        ValidateRead(read);
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, AllowedCount(count), cancellationToken).ConfigureAwait(false);
        ValidateRead(read);
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer[..AllowedCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
        ValidateRead(read);
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int AllowedCount(int requested)
    {
        if (_readBytes < _maxBytes)
        {
            return (int)Math.Min(requested, _maxBytes - _readBytes);
        }

        return Math.Min(requested, 1);
    }

    private void ValidateRead(int read)
    {
        if (read == 0)
        {
            return;
        }

        if (_readBytes >= _maxBytes || read > _maxBytes - _readBytes)
        {
            throw new BoundedHttpException($"Provider response exceeds the {_maxBytes}-byte transport budget.");
        }

        _readBytes += read;
    }
}

/// <summary>
/// Reads '\n'-terminated lines (a trailing '\r' is stripped) from a UTF-8 stream with a per-line
/// character budget and a physical-line budget. Physical lines include blank lines and SSE
/// comments; callers count logical events (NDJSON objects, SSE <c>data:</c> payloads) separately.
/// Invalid UTF-8 fails closed.
/// </summary>
public sealed class BoundedLineReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly int _maxLineCharacters;
    private readonly int _maxPhysicalLines;
    private readonly char[] _buffer = new char[8_192];
    private readonly StringBuilder _line = new();
    private int _position;
    private int _length;
    private bool _endOfStream;

    /// <summary>
    /// Creates a reader over <paramref name="stream"/>. Disposing the reader disposes the stream.
    /// </summary>
    public BoundedLineReader(Stream stream, int maxLineCharacters, int maxPhysicalLines)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        _maxLineCharacters = maxLineCharacters > 0 ? maxLineCharacters : throw new ArgumentOutOfRangeException(nameof(maxLineCharacters));
        _maxPhysicalLines = maxPhysicalLines > 0 ? maxPhysicalLines : throw new ArgumentOutOfRangeException(nameof(maxPhysicalLines));
        _reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 8_192);
    }

    /// <summary>
    /// Physical lines returned so far.
    /// </summary>
    public int PhysicalLines { get; private set; }

    /// <summary>
    /// Returns the next line without its terminator, or null at end of stream. Throws
    /// <see cref="BoundedHttpException"/> when a line or the line count exceeds its budget.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.Clear();
        while (true)
        {
            if (_position >= _length)
            {
                if (_endOfStream)
                {
                    break;
                }

                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    _endOfStream = true;
                    break;
                }
            }

            var newline = Array.IndexOf(_buffer, '\n', _position, _length - _position);
            var end = newline < 0 ? _length : newline;
            var count = end - _position;

            // +1 leaves room for a '\r' that is stripped below.
            if (count > _maxLineCharacters + 1 - _line.Length)
            {
                throw new BoundedHttpException($"Stream line exceeds the {_maxLineCharacters}-character budget.");
            }

            _line.Append(_buffer, _position, count);
            _position = newline < 0 ? _length : newline + 1;
            if (newline >= 0)
            {
                return CompleteLine();
            }
        }

        return _line.Length == 0 ? null : CompleteLine();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _reader.Dispose();
    }

    private string CompleteLine()
    {
        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        if (_line.Length > _maxLineCharacters)
        {
            throw new BoundedHttpException($"Stream line exceeds the {_maxLineCharacters}-character budget.");
        }

        PhysicalLines++;
        if (PhysicalLines > _maxPhysicalLines)
        {
            throw new BoundedHttpException($"Stream exceeds the {_maxPhysicalLines}-line budget.");
        }

        return _line.ToString();
    }
}

/// <summary>
/// Thrown by the provider handler when a connection to the model server was not established
/// within <see cref="BoundedHttp.DefaultConnectTimeout"/> (or the handler's configured bound).
/// </summary>
public sealed class ProviderConnectTimeoutException : IOException
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public ProviderConnectTimeoutException(string message)
        : base(message)
    {
    }
}
