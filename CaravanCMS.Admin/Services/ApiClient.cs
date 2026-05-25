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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
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
        content.Add(fileContent, "file", Path.GetFileName(filePath));

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

    /// <summary>Triggers a file scan on the server's configured Caravan History folder.</summary>
    public async Task<FileScanResultDto> ScanFilesAsync(string? folderPath = null, IProgress<string>? progress = null)
    {
        progress?.Report("Starting file scan...");

        ScanFilesRequest request = new() { FolderPath = folderPath };
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/import/scan-files", request, JsonOpts);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            throw new ApiException($"Scan failed ({(int)response.StatusCode}): {errorBody}");
        }

        FileScanResultDto? result = await response.Content.ReadFromJsonAsync<FileScanResultDto>(JsonOpts);
        return result ?? new FileScanResultDto();
    }

    /// <summary>Links a file to a caravan as a document record.</summary>
    public async Task<DocumentDto> LinkDocumentAsync(LinkDocumentRequest request)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/import/link-document", request, JsonOpts);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new ApiException($"Link failed ({(int)response.StatusCode}): {error}");
        }

        DocumentDto? doc = await response.Content.ReadFromJsonAsync<DocumentDto>(JsonOpts);
        return doc ?? throw new ApiException("Server returned empty document response.");
    }

    /// <summary>Returns all caravans (lightweight summary list).</summary>
    public async Task<List<CaravanSummaryDto>> GetCaravansAsync()
    {
        HttpResponseMessage response = await _http.GetAsync("api/caravans");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CaravanSummaryDto>>(JsonOpts) ?? new();
    }
}

/// <summary>Thrown when the API returns a non-success response or the request fails.</summary>
public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}
