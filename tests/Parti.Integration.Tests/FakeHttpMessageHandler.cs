// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic <see cref="HttpMessageHandler"/> for streaming tests. Keeps tests off the network
/// and lets callers inject canned SSE bodies (or custom responders) without any real transport.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    /// <summary>Every request observed, in call order.</summary>
    public System.Collections.Generic.List<HttpRequestMessage> ReceivedRequests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    /// <summary>
    /// Convenience factory that returns a single canned SSE response for every request. The body
    /// is served through a <see cref="SlowStream"/> that releases <paramref name="chunkSize"/> bytes
    /// per read and optionally waits <paramref name="perChunkDelay"/> between reads — useful for
    /// cancellation tests. Set <paramref name="perChunkDelay"/> to <see cref="TimeSpan.Zero"/> (the
    /// default) for instant reads.
    /// </summary>
    public static FakeHttpMessageHandler FromSse(string sseBody, int chunkSize = int.MaxValue, TimeSpan perChunkDelay = default)
    {
        return new FakeHttpMessageHandler((_, ct) =>
        {
            var bytes = Encoding.UTF8.GetBytes(sseBody ?? string.Empty);
            var stream = new SlowStream(bytes, chunkSize, perChunkDelay);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });
    }

    /// <summary>
    /// Convenience factory that returns the given HTTP status with a plain text body.
    /// </summary>
    public static FakeHttpMessageHandler FromStatus(HttpStatusCode status, string body)
    {
        return new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ReceivedRequests.Add(request);
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only stream that paces byte delivery — emits at most <see cref="_chunkSize"/> bytes per
    /// read and optionally sleeps <see cref="_delay"/> between reads.
    /// </summary>
    private sealed class SlowStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public SlowStream(byte[] data, int chunkSize, TimeSpan delay)
        {
            _data = data ?? Array.Empty<byte>();
            _chunkSize = chunkSize > 0 ? chunkSize : int.MaxValue;
            _delay = delay;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadCore(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ReadCore(buffer, offset, count);
        }

#if NET6_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _data.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(Math.Min(remaining, _chunkSize), buffer.Length);
            _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }
#endif

        private int ReadCore(byte[] buffer, int offset, int count)
        {
            var remaining = _data.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(Math.Min(remaining, _chunkSize), count);
            Array.Copy(_data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
