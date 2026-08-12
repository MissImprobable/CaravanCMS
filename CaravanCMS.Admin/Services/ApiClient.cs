using CaravanCMS.Core;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// HTTP client for the CaravanCMS REST API.
/// Handles authentication, serialization, and error wrapping.
/// All methods throw ApiException on non-success responses.
/// </summary>
public class ApiClient
{
    private readonly HttpClient _http;
    // PropertyNamingPolicy = CamelCase so outgoing request bodies match what CaravanCMS.Worker's
    // Hono routes expect (a plain case-sensitive JSON.parse, not ASP.NET-style case-insensitive
    // model binding). PropertyNameCaseInsensitive keeps incoming responses tolerant either way.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ApiClient(string baseUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    /// <summary>
    /// .NET's MultipartFormDataContent.Add(content, name) doesn't always quote the Content-Disposition
    /// name/filename parameters (it treats quoting as optional for token-safe values per RFC 6266,
    /// which is spec-legal but broke against the Worker's stricter incoming multipart parser — every
    /// upload failed with "Content-Disposition header in FormData part is missing a name" until this
    /// was fixed). Setting the header manually guarantees quoted values every time.
    /// </summary>
    private static void SetFormDataName(HttpContent content, string name, string? fileName = null)
    {
        content.Headers.Remove("Content-Disposition");
        string header = $"form-data; name=\"{name}\"";
        if (fileName is not null) header += $"; filename=\"{fileName}\"";
        content.Headers.TryAddWithoutValidation("Content-Disposition", header);
    }

    private static void AddFormField(MultipartFormDataContent content, string name, string value)
    {
        StringContent part = new(value);
        SetFormDataName(part, name);
        content.Add(part);
    }

    /// <summary>Tests the connection by fetching the stats endpoint. Returns true on success.</summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync()
    {
        try
        {
            HttpResponseMessage response = await _http.GetAsync("api/caravans/stats");
            if (response.IsSuccessStatusCode)
                return (true, "Connected successfully.");
            return (false, $"Server returned {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Returns summary statistics (caravan count, last import, etc.).</summary>
    public async Task<ApiStatsDto?> GetStatsAsync()
    {
        HttpResponseMessage response = await _http.GetAsync("api/caravans/stats");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiStatsDto>(JsonOpts);
    }

    /// <summary>Uploads an Excel file for MechanicDesk import and returns the result.</summary>
    public async Task<ImportResultDto> ImportMechanicDeskAsync(string filePath, IProgress<string>? progress = null)
    {
        progress?.Report($"Reading {Path.GetFileName(filePath)}...");

        await using FileStream fileStream = File.OpenRead(filePath);
        using MultipartFormDataContent content = new();
        using StreamContent fileContent = new(fileStream);

        string contentType = filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : "application/vnd.ms-excel";

        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        SetFormDataName(fileContent, "file", Path.GetFileName(filePath));
        content.Add(fileContent);

        progress?.Report("Uploading to server...");
        HttpResponseMessage response = await _http.PostAsync("api/import/mechanicdesk", content);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            throw new ApiException($"Import failed ({(int)response.StatusCode}): {errorBody}");
        }

        progress?.Report("Processing results...");
        ImportResultDto? result = await response.Content.ReadFromJsonAsync<ImportResultDto>(JsonOpts);
        return result ?? new ImportResultDto { Errors = { "Server returned empty response." } };
    }

    /// <summary>
    /// Returns every Document row (no filters) — used by the document sync service to know
    /// which local FilePaths are already linked, so it doesn't re-upload them.
    /// </summary>
    public async Task<List<DocumentDto>> GetAllDocumentsAsync()
    {
        HttpResponseMessage response = await _http.GetAsync("api/documents");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DocumentDto>>(JsonOpts) ?? new();
    }

    /// <summary>
    /// Uploads a local file's bytes directly to the server (which resizes images and stores them
    /// in R2) and creates the linking Document row in one call. This replaces the old model of
    /// "point the server at a file path already on its own disk" — Workers has no local filesystem
    /// to read from, so the actual bytes must be sent.
    /// </summary>
    public async Task<DocumentDto> UploadDocumentAsync(string filePath, string registrationNumber, string documentType, string linkMethod, string? sourceFilePath = null)
    {
        using MultipartFormDataContent content = new();
        HttpContent fileContent;
        bool alreadyResized = false;

        // Resize here rather than relying on the Worker's own resize step — @cf-wasm/photon has
        // an unresolved WASM bug there (see ImageResizeService's doc comment), and doing it
        // client-side with System.Drawing avoids that entirely for this, the dominant upload path.
        // Also tells the Worker to skip its own resize (alreadyResized=true below) — that step is
        // CPU-heavy enough to occasionally trip the free tier's 10ms-per-request limit (Cloudflare
        // error 1102), which is the most likely actual trigger for the WASM corruption bug.
        if (ImageResizeService.IsResizable(filePath))
        {
            (byte[] bytes, string fileName) = ImageResizeService.Resize(filePath);
            fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            SetFormDataName(fileContent, "file", fileName);
            alreadyResized = true;
        }
        else
        {
            FileStream fileStream = File.OpenRead(filePath);
            fileContent = new StreamContent(fileStream);
            SetFormDataName(fileContent, "file", Path.GetFileName(filePath));
        }

        using (fileContent)
        {
            content.Add(fileContent);
            AddFormField(content, "registrationNumber", registrationNumber);
            AddFormField(content, "documentType", documentType);
            AddFormField(content, "linkMethod", linkMethod);
            if (alreadyResized) AddFormField(content, "alreadyResized", "true");
            if (sourceFilePath is not null) AddFormField(content, "filePath", sourceFilePath);

            HttpResponseMessage response = await _http.PostAsync("api/documents", content);
            return await HandleUploadResponseAsync(response);
        }
    }

    private async Task<DocumentDto> HandleUploadResponseAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new ApiException($"Upload failed ({(int)response.StatusCode}): {error}");
        }

        DocumentDto? doc = await response.Content.ReadFromJsonAsync<DocumentDto>(JsonOpts);
        return doc ?? throw new ApiException("Server returned empty document response.");
    }

    /// <summary>Returns all documents linked via inferred/fuzzy methods, for admin review.</summary>
    public async Task<InferredLinksReportDto> GetInferredLinksReportAsync()
    {
        HttpResponseMessage response = await _http.GetAsync("api/documents/inferred-report");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InferredLinksReportDto>(JsonOpts)
               ?? new InferredLinksReportDto();
    }

    /// <summary>
    /// Creates a minimal placeholder caravan for a registration number found on disk with no
    /// matching database row (attached to a shared "Unassigned" customer server-side). Returns
    /// null on 409 (already exists — a race with another sync run) rather than throwing, since
    /// that's an expected, harmless outcome for the caller to just re-fetch and continue past.
    /// </summary>
    public async Task<CaravanSummaryDto?> CreatePlaceholderCaravanAsync(string registrationNumber, string? vin)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/caravans",
            new { registrationNumber, vin }, JsonOpts);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"Create caravan failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<CaravanSummaryDto>(JsonOpts);
    }

    /// <summary>Returns all caravans (lightweight summary list).</summary>
    public async Task<List<CaravanSummaryDto>> GetCaravansAsync()
    {
        HttpResponseMessage response = await _http.GetAsync("api/caravans");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CaravanSummaryDto>>(JsonOpts) ?? new();
    }

    /// <summary>Searches customers by name, email, phone, or customer number.</summary>
    public async Task<List<CustomerLookupDto>> SearchCustomersAsync(string query)
    {
        string encoded = Uri.EscapeDataString(query);
        HttpResponseMessage response = await _http.GetAsync($"api/customers/search?q={encoded}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CustomerLookupDto>>(JsonOpts) ?? new();
    }

    /// <summary>Returns every conversation thread for a customer, newest activity first.</summary>
    public async Task<List<ConversationDto>> GetCustomerConversationsAsync(int customerId)
    {
        HttpResponseMessage response = await _http.GetAsync($"api/customers/{customerId}/conversations");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ConversationDto>>(JsonOpts) ?? new();
    }

    /// <summary>Starts a new conversation thread for a customer (or returns the existing one, if matched by ExternalConversationId).</summary>
    public async Task<ConversationDto> CreateConversationAsync(CreateConversationRequest request)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/conversations", request, JsonOpts);
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"Create conversation failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new ApiException("Server returned empty conversation response.");
    }

    /// <summary>Appends a message (email, call, note, meeting) to an existing conversation.</summary>
    public async Task<CommunicationLogDto> AddMessageAsync(int conversationId, LogMessageRequest request)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/messages", request, JsonOpts);
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"Log message failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<CommunicationLogDto>(JsonOpts)
               ?? throw new ApiException("Server returned empty message response.");
    }

    /// <summary>Attaches a purpose tag to a conversation, creating the tag if it doesn't already exist.</summary>
    public async Task<ConversationDto> AddTagAsync(int conversationId, string tagName)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/tags",
            new AttachTagRequest { Name = tagName }, JsonOpts);
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"Add tag failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new ApiException("Server returned empty conversation response.");
    }

    /// <summary>Removes a tag from a conversation.</summary>
    public async Task<ConversationDto> RemoveTagAsync(int conversationId, int tagId)
    {
        HttpResponseMessage response = await _http.DeleteAsync($"api/conversations/{conversationId}/tags/{tagId}");
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"Remove tag failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<ConversationDto>(JsonOpts)
               ?? throw new ApiException("Server returned empty conversation response.");
    }
}

/// <summary>Thrown when the API returns a non-success response or the request fails.</summary>
public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}
