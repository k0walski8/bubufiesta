using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;

namespace octo_fiesta.Services.Local;

/// <summary>
/// Local library service implementation
/// Uses a simple JSON file to store mappings (can be replaced with a database)
/// </summary>
public class LocalLibraryService : ILocalLibraryService
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".aiff", ".alac", ".opus", ".wma"
    };
    private static readonly Regex DuplicateSuffixRegex = new(@"\s\(\d+\)$", RegexOptions.Compiled);
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _mappingFilePath;
    private readonly string _downloadDirectory;
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<LocalLibraryService> _logger;
    private Dictionary<string, LocalSongMapping>? _mappings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    // Debounce to avoid triggering too many scans
    private DateTime _lastScanTrigger = DateTime.MinValue;
    private readonly TimeSpan _scanDebounceInterval = TimeSpan.FromSeconds(30);

    public LocalLibraryService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<LocalLibraryService> logger)
    {
        _downloadDirectory = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _mappingFilePath = Path.Combine(_downloadDirectory, ".mappings.json");
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
        
        if (!Directory.Exists(_downloadDirectory))
        {
            Directory.CreateDirectory(_downloadDirectory);
        }
    }

    public async Task<string?> GetLocalPathForExternalSongAsync(string externalProvider, string externalId)
    {
        var mappings = await LoadMappingsAsync();
        var key = $"{externalProvider}:{externalId}";
        
        if (mappings.TryGetValue(key, out var mapping) && File.Exists(mapping.LocalPath))
        {
            return mapping.LocalPath;
        }
        
        return null;
    }

public async Task RegisterDownloadedSongAsync(Song song, string localPath, string? downloadedQuality = null)
    {
        if (song.ExternalProvider == null || song.ExternalId == null) return;
        
        // Load mappings first (this acquires the lock internally if needed)
        var mappings = await LoadMappingsAsync();
        
        await _lock.WaitAsync();
        try
        {
            var key = $"{song.ExternalProvider}:{song.ExternalId}";
            
            mappings[key] = new LocalSongMapping
            {
                ExternalProvider = song.ExternalProvider,
                ExternalId = song.ExternalId,
                LocalPath = localPath,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                DownloadedAt = DateTime.UtcNow,
                DownloadedQuality = downloadedQuality
            };
            
            await SaveMappingsAsync(mappings);
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public async Task<LocalSongMapping?> GetMappingForExternalSongAsync(string externalProvider, string externalId)
    {
        var mappings = await LoadMappingsAsync();
        var key = $"{externalProvider}:{externalId}";
        
        if (mappings.TryGetValue(key, out var mapping) && File.Exists(mapping.LocalPath))
        {
            return mapping;
        }
        
        return null;
    }

    public async Task<string?> GetLocalIdForExternalSongAsync(string externalProvider, string externalId)
    {
        // For now, return null as we don't yet have integration
        // with the Subsonic server to retrieve local ID after scan
        await Task.CompletedTask;
        return null;
    }

    public (bool isExternal, string? provider, string? externalId) ParseSongId(string songId)
    {
        var (isExternal, provider, _, externalId) = ParseExternalId(songId);
        return (isExternal, provider, externalId);
    }

    public (bool isExternal, string? provider, string? type, string? externalId) ParseExternalId(string id)
    {
        var normalizedId = NormalizeExternalResourceId(id);

        if (!normalizedId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, null, null);
        }
        
        var parts = normalizedId.Split('-');
        
        // Known types for the new format
        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "song", "album", "artist" };
        
        // New format: ext-{provider}-{type}-{id} (e.g., ext-deezer-artist-259)
        // Only use new format if parts[2] is a known type
        if (parts.Length >= 4 && knownTypes.Contains(parts[2]))
        {
            var provider = parts[1].ToLowerInvariant();
            var type = parts[2].ToLowerInvariant();
            var externalId = string.Join("-", parts.Skip(3)); // Handle IDs with dashes
            return (true, provider, type, externalId);
        }
        
        // Legacy format: ext-{provider}-{id} (assumes "song" type for backward compatibility)
        // This handles both 3-part IDs and 4+ part IDs where parts[2] is NOT a known type
        if (parts.Length >= 3)
        {
            var provider = parts[1].ToLowerInvariant();
            var externalId = string.Join("-", parts.Skip(2)); // Everything after provider is the ID
            return (true, provider, "song", externalId);
        }
        
        return (false, null, null, null);
    }

    public async Task<int> RemoveDuplicateTracksInAlbumFolderAsync(string trackPath)
    {
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            return 0;
        }

        var normalizedTrackPath = Path.GetFullPath(trackPath);
        var albumFolder = Path.GetDirectoryName(normalizedTrackPath);
        if (string.IsNullOrWhiteSpace(albumFolder) || !Directory.Exists(albumFolder))
        {
            return 0;
        }

        var audioFiles = Directory
            .GetFiles(albumFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(IsAudioFile)
            .Select(path => new FileInfo(path))
            .ToList();

        if (audioFiles.Count < 2)
        {
            return 0;
        }

        var deletedPaths = new List<string>();

        foreach (var group in audioFiles.GroupBy(file => GetNormalizedDuplicateKey(file.Name), StringComparer.OrdinalIgnoreCase))
        {
            var files = group.ToList();
            if (files.Count < 2)
            {
                continue;
            }

            var keepFile = SelectFileToKeep(files, normalizedTrackPath);
            foreach (var duplicateFile in files)
            {
                if (string.Equals(duplicateFile.FullName, keepFile.FullName, PathComparison))
                {
                    continue;
                }

                try
                {
                    File.Delete(duplicateFile.FullName);
                    deletedPaths.Add(duplicateFile.FullName);

                    var lyricsPath = Path.ChangeExtension(duplicateFile.FullName, ".lrc");
                    if (File.Exists(lyricsPath))
                    {
                        File.Delete(lyricsPath);
                    }

                    _logger.LogInformation("Deleted duplicate track file: {Path}", duplicateFile.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete duplicate track file: {Path}", duplicateFile.FullName);
                }
            }
        }

        if (deletedPaths.Count > 0)
        {
            await RemoveDeletedMappingsAsync(deletedPaths);
        }

        return deletedPaths.Count;
    }

    private static string NormalizeExternalResourceId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        var normalized = id.Trim();
        if (normalized.Contains('%'))
        {
            try
            {
                normalized = Uri.UnescapeDataString(normalized);
            }
            catch (UriFormatException)
            {
                // Keep original value if decoding fails.
            }
        }
        var schemeSeparatorIndex = normalized.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex < 0)
        {
            return normalized;
        }

        var withoutScheme = normalized[(schemeSeparatorIndex + 3)..];
        if (string.IsNullOrWhiteSpace(withoutScheme))
        {
            return withoutScheme;
        }

        var delimiterIndex = withoutScheme.IndexOfAny(new[] { '?', '#' });
        if (delimiterIndex >= 0)
        {
            withoutScheme = withoutScheme[..delimiterIndex];
        }

        // Common MA/OpenSubsonic form: track://ext-provider-song-123
        if (withoutScheme.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return withoutScheme;
        }

        // Some clients include an extra provider scope: track://provider-instance/ext-provider-song-123
        var slashIndex = withoutScheme.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < withoutScheme.Length - 1)
        {
            var tail = withoutScheme[(slashIndex + 1)..];
            if (tail.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
            {
                return tail;
            }
        }

        return withoutScheme;
    }

    private static bool IsAudioFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && SupportedAudioExtensions.Contains(extension);
    }

    private static string GetNormalizedDuplicateKey(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var normalized = DuplicateSuffixRegex.Replace(nameWithoutExtension, string.Empty).Trim();
        return normalized.ToLowerInvariant();
    }

    private static FileInfo SelectFileToKeep(List<FileInfo> candidates, string preferredPath)
    {
        var preferred = candidates.FirstOrDefault(file =>
            string.Equals(file.FullName, preferredPath, PathComparison));
        if (preferred != null)
        {
            return preferred;
        }

        return candidates
            .OrderByDescending(file => !DuplicateSuffixRegex.IsMatch(Path.GetFileNameWithoutExtension(file.Name)))
            .ThenByDescending(file => GetExtensionQualityScore(file.Extension))
            .ThenByDescending(file => file.Length)
            .ThenByDescending(file => file.LastWriteTimeUtc)
            .First();
    }

    private static int GetExtensionQualityScore(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".flac" => 60,
            ".wav" or ".aiff" or ".alac" => 50,
            ".m4a" or ".aac" => 40,
            ".ogg" or ".opus" => 30,
            ".mp3" => 20,
            ".wma" => 10,
            _ => 0
        };
    }

    private async Task RemoveDeletedMappingsAsync(List<string> deletedPaths)
    {
        var deletedPathSet = new HashSet<string>(
            deletedPaths
                .Select(TryGetNormalizedPath)
                .OfType<string>(),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var mappings = await LoadMappingsAsync();

        await _lock.WaitAsync();
        try
        {
            var keysToRemove = mappings
                .Where(kvp =>
                {
                    var normalizedPath = TryGetNormalizedPath(kvp.Value.LocalPath);
                    return normalizedPath != null && deletedPathSet.Contains(normalizedPath);
                })
                .Select(kvp => kvp.Key)
                .ToList();

            if (keysToRemove.Count == 0)
            {
                return;
            }

            foreach (var key in keysToRemove)
            {
                mappings.Remove(key);
            }

            await SaveMappingsAsync(mappings);
            _logger.LogInformation("Removed {Count} stale mapping(s) for deleted duplicate tracks", keysToRemove.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string? TryGetNormalizedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, LocalSongMapping>> LoadMappingsAsync()
    {
        // Fast path: return cached mappings if available
        if (_mappings != null) return _mappings;
        
        // Slow path: acquire lock to load from file (prevents race condition)
        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_mappings != null) return _mappings;
            
            if (File.Exists(_mappingFilePath))
            {
                var json = await File.ReadAllTextAsync(_mappingFilePath);
                _mappings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, LocalSongMapping>>(json) 
                            ?? new Dictionary<string, LocalSongMapping>();
            }
            else
            {
                _mappings = new Dictionary<string, LocalSongMapping>();
            }
            
            return _mappings;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveMappingsAsync(Dictionary<string, LocalSongMapping> mappings)
    {
        _mappings = mappings;
        var json = System.Text.Json.JsonSerializer.Serialize(mappings, new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        await File.WriteAllTextAsync(_mappingFilePath, json);
    }

    public string GetDownloadDirectory() => _downloadDirectory;

    public async Task<bool> TriggerLibraryScanAsync()
    {
        // Debounce: avoid triggering too many successive scans
        var now = DateTime.UtcNow;
        if (now - _lastScanTrigger < _scanDebounceInterval)
        {
            _logger.LogDebug("Scan debounced - last scan was {Elapsed}s ago", 
                (now - _lastScanTrigger).TotalSeconds);
            return true;
        }

        try
        {
            var url = BuildSubsonicApiUrl("rest/startScan.view");
            _logger.LogInformation("Triggering Subsonic library scan...");
            
            using var response = await _httpClient.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to trigger Subsonic scan: HTTP {StatusCode}. Response: {Response}",
                    (int)response.StatusCode,
                    content);
                return false;
            }

            if (TryParseSubsonicResponse(content, out var status, out var errorCode, out var errorMessage))
            {
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Subsonic scan request failed: status={Status}, code={Code}, message={Message}",
                        status ?? "unknown",
                        errorCode?.ToString() ?? "n/a",
                        errorMessage ?? "n/a");
                    return false;
                }
            }

            _lastScanTrigger = now;
            _logger.LogInformation("Subsonic scan triggered successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Subsonic library scan");
            return false;
        }
    }

    public async Task<ScanStatus?> GetScanStatusAsync()
    {
        try
        {
            var url = BuildSubsonicApiUrl("rest/getScanStatus.view");
            
            using var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                
                if (doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse))
                {
                    if (subsonicResponse.TryGetProperty("status", out var statusElement) &&
                        string.Equals(statusElement.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = subsonicResponse.TryGetProperty("error", out var error) &&
                                   error.TryGetProperty("code", out var codeElement)
                            ? codeElement.GetInt32()
                            : (int?)null;
                        var message = subsonicResponse.TryGetProperty("error", out error) &&
                                      error.TryGetProperty("message", out var msgElement)
                            ? msgElement.GetString()
                            : null;

                        _logger.LogWarning(
                            "Failed to get Subsonic scan status: code={Code}, message={Message}",
                            code?.ToString() ?? "n/a",
                            message ?? "n/a");
                        return null;
                    }

                    if (subsonicResponse.TryGetProperty("scanStatus", out var scanStatus))
                    {
                        return new ScanStatus
                        {
                            Scanning = scanStatus.TryGetProperty("scanning", out var scanning) && scanning.GetBoolean(),
                            Count = scanStatus.TryGetProperty("count", out var count) ? count.GetInt32() : null
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Subsonic scan status");
        }
        
        return null;
    }

    private string BuildSubsonicApiUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new InvalidOperationException("Subsonic URL is not configured");
        }

        var normalizedBase = _subsonicSettings.Url.TrimEnd('/') + "/";
        var baseUri = new Uri(normalizedBase, UriKind.Absolute);
        var endpointUri = new Uri(baseUri, endpoint.TrimStart('/'));

        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in QueryHelpers.ParseQuery(baseUri.Query))
        {
            var value = kv.Value.ToString();
            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrWhiteSpace(value))
            {
                queryParams[kv.Key] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(baseUri.UserInfo))
        {
            var userInfo = baseUri.UserInfo;
            var separatorIndex = userInfo.IndexOf(':');
            if (separatorIndex > 0)
            {
                var username = Uri.UnescapeDataString(userInfo[..separatorIndex]);
                var password = Uri.UnescapeDataString(userInfo[(separatorIndex + 1)..]);
                if (!string.IsNullOrWhiteSpace(username) && !queryParams.ContainsKey("u"))
                {
                    queryParams["u"] = username;
                }
                if (!string.IsNullOrWhiteSpace(password) &&
                    !queryParams.ContainsKey("p") &&
                    !queryParams.ContainsKey("t"))
                {
                    queryParams["p"] = password;
                }
            }
        }

        queryParams["f"] = "json";
        queryParams["v"] = "1.16.1";
        queryParams["c"] = "octo-fiesta";

        return QueryHelpers.AddQueryString(endpointUri.ToString(), queryParams);
    }

    private static bool TryParseSubsonicResponse(
        string content,
        out string? status,
        out int? errorCode,
        out string? errorMessage)
    {
        status = null;
        errorCode = null;
        errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var response))
            {
                return false;
            }

            if (response.TryGetProperty("status", out var statusElement))
            {
                status = statusElement.GetString();
            }

            if (response.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeElement))
                {
                    errorCode = codeElement.GetInt32();
                }

                if (error.TryGetProperty("message", out var messageElement))
                {
                    errorMessage = messageElement.GetString();
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Represents the mapping between an external song and its local file
/// </summary>
public class LocalSongMapping
{
    public string ExternalProvider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string? LocalSubsonicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public DateTime DownloadedAt { get; set; }
    
    /// <summary>
    /// Quality of the downloaded file (e.g., "FLAC", "MP3_320", "MP3_128")
    /// Null for legacy downloads before quality tracking was added
    /// </summary>
    public string? DownloadedQuality { get; set; }
}
