using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class FanartClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        private const string BaseUrl = "https://webservice.fanart.tv/v3/tv";

        public FanartClient(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<FanartTvResponse?> GetAsync(string tvdbId, string? personalApiKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tvdbId))
            {
                return null;
            }

            var url = $"{BaseUrl}/{tvdbId}?api_key={Uri.EscapeDataString(personalApiKey ?? string.Empty)}";

            try
            {
                // A network error here used to throw out of GetImages and lose the TVDB images too.
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Fanart.tv request failed for tvdb {TvdbId} with status {Status}", tvdbId, response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<FanartTvResponse>(json, _jsonOptions);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Fanart.tv request failed for tvdb {TvdbId}", tvdbId);
                return null;
            }
        }

        public Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetAsync(url, cancellationToken);
        }
    }
}
