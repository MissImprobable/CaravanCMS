using CaravanCMS.Core;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CaravanCMS.Client.Services;

/// <summary>HTTP client for the CaravanCMS REST API — read-only operations for the Client app.</summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string BaseUrl { get; }

    public ApiClient(string baseUrl, string apiKey)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync()
    {
        try
        {
            HttpResponseMessage r = await _http.GetAsync("api/caravans/stats");
            return r.IsSuccessStatusCode
                ? (true, $"Connected to {BaseUrl}")
                : (false, $"{(int)r.StatusCode}: {r.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<CaravanSummaryDto>> SearchAsync(string query)
    {
        string encoded = Uri.EscapeDataString(query);
        HttpResponseMessage r = await _http.GetAsync($"api/caravans/search?q={encoded}");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<List<CaravanSummaryDto>>(JsonOpts) ?? new();
    }

    public async Task<List<CaravanSummaryDto>> GetAllCaravansAsync()
    {
        HttpResponseMessage r = await _http.GetAsync("api/caravans");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<List<CaravanSummaryDto>>(JsonOpts) ?? new();
    }

    public async Task<CaravanDetailDto?> GetCaravanDetailAsync(string rego)
    {
        HttpResponseMessage r = await _http.GetAsync($"api/caravans/{Uri.EscapeDataString(rego)}");
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<CaravanDetailDto>(JsonOpts);
    }

    public async Task<ApiStatsDto?> GetStatsAsync()
    {
        HttpResponseMessage r = await _http.GetAsync("api/caravans/stats");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ApiStatsDto>(JsonOpts);
    }

    public async Task<byte[]> DownloadDocumentAsync(int documentId)
    {
        HttpResponseMessage r = await _http.GetAsync($"api/documents/{documentId}/download");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync();
    }

    public string GetDownloadUrl(int documentId) => $"{BaseUrl}/api/documents/{documentId}/download";
}
