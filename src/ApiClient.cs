using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlmapsServerPlugin
{
    internal enum ApiFailure
    {
        None,
        Network,
        Timeout,
        Canceled,
    }

    internal sealed class ApiResult
    {
        public int StatusCode;
        public ApiFailure Failure;
        public string FailureMessage;
        public Dictionary<string, object> Body;

        public bool HasResponse
        {
            get { return Failure == ApiFailure.None; }
        }

        public bool IsSuccess
        {
            get { return HasResponse && StatusCode >= 200 && StatusCode <= 299; }
        }

        public static ApiResult FromResponse(int statusCode, string body)
        {
            return new ApiResult { StatusCode = statusCode, Failure = ApiFailure.None, Body = Json.TryParseObject(body) };
        }

        public static ApiResult FromFailure(ApiFailure failure, string message)
        {
            return new ApiResult { StatusCode = 0, Failure = failure, FailureMessage = message };
        }

        public string GetString(string name)
        {
            object value;
            if (Body != null && Body.TryGetValue(name, out value))
            {
                return value as string;
            }
            return null;
        }

        public double GetNumber(string name, double fallback)
        {
            object value;
            if (Body != null && Body.TryGetValue(name, out value) && value is double)
            {
                return (double)value;
            }
            return fallback;
        }

        // Goes into logs, so it stays free of credentials.
        public string Describe(int timeoutSeconds)
        {
            switch (Failure)
            {
                case ApiFailure.Timeout:
                    return "timed out after " + timeoutSeconds + "s";
                case ApiFailure.Canceled:
                    return "canceled";
                case ApiFailure.Network:
                    return "network error: " + Truncate(FailureMessage, 200);
            }
            string error = GetString("error");
            return "HTTP " + StatusCode + (string.IsNullOrEmpty(error) ? "" : " \"" + Truncate(error, 100) + "\"");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    internal interface IApiTransport
    {
        Task<ApiResult> PostJsonAsync(string url, string json, string bearerToken, int timeoutSeconds, CancellationToken cancellationToken);
    }

    // HttpClient.Timeout cannot be changed after the first request, so it stays infinite and each request times out through its own token.
    internal sealed class HttpApiTransport : IApiTransport
    {
        private static readonly object ClientLock = new object();
        private static HttpClient _client;

        private readonly string _userAgent;

        public HttpApiTransport(string userAgent)
        {
            _userAgent = userAgent;
        }

        private static HttpClient Client
        {
            get
            {
                lock (ClientLock)
                {
                    if (_client == null)
                    {
                        // Following a redirect would turn a POST into a GET, so 3xx is reported as it is.
                        HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false };
                        _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
                    }
                    return _client;
                }
            }
        }

        public async Task<ApiResult> PostJsonAsync(string url, string json, string bearerToken, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CancellationTokenSource timeout = null;
            CancellationTokenSource linked = null;
            HttpRequestMessage request = null;
            try
            {
                timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
                request.Headers.ExpectContinue = false;
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (!string.IsNullOrEmpty(bearerToken))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerToken);
                }
                using (HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false))
                {
                    string body = null;
                    if (response.Content != null)
                    {
                        body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                    return ApiResult.FromResponse((int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                // Mono can wrap a cancellation in WebException, so the token decides instead of the exception type.
                if (cancellationToken.IsCancellationRequested)
                {
                    return ApiResult.FromFailure(ApiFailure.Canceled, "canceled");
                }
                if (timeout != null && timeout.IsCancellationRequested)
                {
                    return ApiResult.FromFailure(ApiFailure.Timeout, "timeout");
                }
                return ApiResult.FromFailure(ApiFailure.Network, Flatten(ex));
            }
            finally
            {
                if (request != null)
                {
                    request.Dispose();
                }
                if (linked != null)
                {
                    linked.Dispose();
                }
                if (timeout != null)
                {
                    timeout.Dispose();
                }
            }
        }

        private static string Flatten(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            Exception current = ex;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
                current = current.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }
}
