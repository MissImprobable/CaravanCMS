using CaravanCMS.Core;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CaravanCMS.Client.Services;

/// <summary>HTTP client for the CaravanCMS REST API.</summary>
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

    /// <summary>Saves edits made on the Vehicle Info tab. Returns the updated caravan record.</summary>
    public async Task<CaravanDetailDto> UpdateCaravanAsync(string rego, UpdateCaravanRequest request)
    {
        HttpResponseMessage r = await _http.PatchAsync($"api/caravans/{Uri.EscapeDataString(rego)}",
            JsonContent.Create(request, options: JsonOpts));
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<CaravanDetailDto>(JsonOpts)
               ?? throw new InvalidOperationException("Server returned empty caravan response.");
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

    /// <summary>Starts a new conversation thread for a customer (or returns the existing one, if matched by ExternalConversationId).</summary>
    public async Task<ConversationDto> CreateConversationAsync(CreateConversationRequest request)
    {
        HttpResponseMessage r = await _http.PostAsJsonAsync("api/conversations", request, JsonOpts);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new InvalidOperationException("Server returned empty conversation response.");
    }

    /// <summary>Appends a message (call, note, meeting, email) to an existing conversation.</summary>
    public async Task<CommunicationLogDto> AddMessageAsync(int conversationId, LogMessageRequest request)
    {
        HttpResponseMessage r = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/messages", request, JsonOpts);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<CommunicationLogDto>(JsonOpts)
               ?? throw new InvalidOperationException("Server returned empty message response.");
    }

    /// <summary>Attaches a purpose tag to a conversation, creating the tag if it doesn't already exist.</summary>
    public async Task<ConversationDto> AddTagAsync(int conversationId, string tagName)
    {
        HttpResponseMessage r = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/tags",
            new AttachTagRequest { Name = tagName }, JsonOpts);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new InvalidOperationException("Server returned empty conversation response.");
    }

    /// <summary>Removes a tag from a conversation. The tag itself remains available for reuse.</summary>
    public async Task<ConversationDto> RemoveTagAsync(int conversationId, int tagId)
    {
        HttpResponseMessage r = await _http.DeleteAsync($"api/conversations/{conversationId}/tags/{tagId}");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new InvalidOperationException("Server returned empty conversation response.");
    }
}
