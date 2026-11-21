using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TestingBot.Internal
{
    /// <summary>
    /// Downloads the tunnel. Redirects are followed by hand, so a redirect from https to http
    /// can be refused: the download ends up being executed, so it should not travel in the clear.
    /// </summary>
    internal sealed class Downloader
    {
        public const int MaxRedirects = 5;

        private readonly HttpClient _client;
        private readonly TimeSpan _idleTimeout;

        public Downloader(HttpMessageHandler? handler = null, TimeSpan? idleTimeout = null)
        {
            handler ??= new HttpClientHandler { AllowAutoRedirect = false };
            _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd($"testingbot-tunnel-dotnet/{typeof(Downloader).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
            _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60);
        }

        /// <summary>Resolve the target of a redirect, refusing one that drops the encryption.</summary>
        public static Uri ResolveRedirect(Uri current, string location)
        {
            var redirect = new Uri(current, location);
            if (current.Scheme == Uri.UriSchemeHttps && redirect.Scheme != Uri.UriSchemeHttps)
            {
                throw new TunnelException($"Refusing to follow the redirect from {Origin(current)} to {Origin(redirect)}, the download would no longer be encrypted");
            }
            return redirect;
        }

        private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

        /// <summary>
        /// Download a file. It is written to a temporary file first and renamed once it is complete,
        /// so a half finished download never ends up at the destination and downloads running next
        /// to each other do not write over each other.
        /// </summary>
        public async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
        {
            var uri = new Uri(url);
            for (var redirects = 0; ; redirects++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_idleTimeout);

                HttpResponseMessage response;
                try
                {
                    response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TunnelException($"Timed out after {(int)_idleTimeout.TotalMilliseconds}ms while downloading {uri}");
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status >= 300 && status < 400 && response.Headers.Location != null)
                    {
                        if (redirects >= MaxRedirects)
                        {
                            throw new TunnelException($"Too many redirects (max {MaxRedirects}) for {uri}");
                        }
                        uri = ResolveRedirect(uri, response.Headers.Location.OriginalString);
                        continue;
                    }

                    if (status >= 400)
                    {
                        throw new TunnelException($"Could not download {uri}, statusCode: {status}");
                    }

                    var temporary = $"{destination}.{CurrentProcessId()}.{RandomSuffix()}.download";
                    try
                    {
                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            var buffer = new byte[81920];
                            while (true)
                            {
                                timeout.CancelAfter(_idleTimeout);
                                int read;
                                try
                                {
                                    read = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                }
                                catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is OperationCanceledException || timeout.IsCancellationRequested))
                                {
                                    throw new TunnelException($"Timed out after {(int)_idleTimeout.TotalMilliseconds}ms while downloading {uri}");
                                }
                                if (read == 0)
                                {
                                    break;
                                }
                                await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        FileSystem.Replace(temporary, destination);
                    }
                    finally
                    {
                        FileSystem.TryDelete(temporary);
                    }
                    return;
                }
            }
        }

        /// <summary>Ask where a URL redirects to, without following the redirect. Null when it does not redirect.</summary>
        public async Task<Uri?> RedirectLocationAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var uri = new Uri(url);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancel.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TunnelException($"Timed out after {(int)timeout.TotalMilliseconds}ms while reaching {uri}");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status >= 300 && status < 400 && response.Headers.Location != null)
                {
                    return ResolveRedirect(uri, response.Headers.Location.OriginalString);
                }
                if (status >= 400)
                {
                    throw new TunnelException($"Could not reach {uri}, statusCode: {status}");
                }
                return null;
            }
        }

        internal static string RandomSuffix()
        {
            var bytes = new byte[6];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static int CurrentProcessId()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.Id;
        }
    }
}
