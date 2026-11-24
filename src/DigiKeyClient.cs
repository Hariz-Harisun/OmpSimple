using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmpSimple.Models;

namespace OmpSimple;

public sealed class DigiKeyClient : IDisposable
{
    private readonly HttpClient _http;
    private string _accessToken = "";
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DigiKeyClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.digikey.com/");
    }

    public async Task<(string? Token, int StatusCode, string? ResponseBody)> GetTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
            return (_accessToken, 200, null);

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", Credentials.ClientId),
            new KeyValuePair<string, string>("client_secret", Credentials.ClientSecret),
            new KeyValuePair<string, string>("grant_type", "client_credentials")
        });

        var resp = await _http.PostAsync("v1/oauth2/token", body, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, responseBody);

        var tokenResp = JsonSerializer.Deserialize<DigiKeyTokenResponse>(responseBody, _jsonOptions);
        if (tokenResp == null)
            return (null, (int)resp.StatusCode, responseBody);

        _accessToken = tokenResp.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResp.ExpiresIn);
        return (_accessToken, (int)resp.StatusCode, responseBody);
    }

    public async Task<(DigiKeySearchResponse? Response, int StatusCode, string? RateLimitLimit, string? RateLimitRemaining, string? ResponseBody)> SearchAsync(string mpn, CancellationToken ct = default)
    {
        var (token, tokenStatusCode, tokenResponseBody) = await GetTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return (null, tokenStatusCode == 200 ? 401 : tokenStatusCode, null, null, tokenResponseBody);

        var request = new HttpRequestMessage(HttpMethod.Post, "products/v4/search/keyword")
        {
            Content = JsonContent.Create(new { Keywords = mpn, Limit = 10, Offset = 0 })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-DIGIKEY-Client-Id", Credentials.ClientId);
        if (!string.IsNullOrWhiteSpace(Credentials.AccountId))
            request.Headers.Add("X-DIGIKEY-Account-Id", Credentials.AccountId);
        request.Headers.Add("X-DIGIKEY-Locale-Site", "US");
        request.Headers.Add("X-DIGIKEY-Locale-Language", "en");
        request.Headers.Add("X-DIGIKEY-Locale-Currency", "USD");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(request, ct);
        var rateLimitLimit = resp.Headers.TryGetValues("X-RateLimit-Limit", out var vals) ? vals.FirstOrDefault() : null;
        var rateLimitRemaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var vals2) ? vals2.FirstOrDefault() : null;

        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, rateLimitLimit, rateLimitRemaining, responseBody);

        return (JsonSerializer.Deserialize<DigiKeySearchResponse>(responseBody, _jsonOptions),
            (int)resp.StatusCode, rateLimitLimit, rateLimitRemaining, responseBody);
    }

    public void Dispose() => _http.Dispose();
}

file static class JsonContent
{
    public static StringContent Create(object obj, JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(obj, options ?? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}