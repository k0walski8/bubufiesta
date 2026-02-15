using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.SquidWTF;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Download service implementation using SquidWTF API
/// Supports both Qobuz and Tidal backends with automatic instance failover for Tidal
/// No decryption needed - SquidWTF returns direct streaming URLs
/// </summary>
public class SquidWTFDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SquidWTFSettings _squidWTFSettings;
    private readonly SquidWTFInstanceManager _instanceManager;
    
    // Static Qobuz API endpoint
    private const string QobuzBaseUrl = "https://qobuz.squid.wtf";
    
    // Required headers
    private const string QobuzCountryHeader = "Token-Country";
    private const string QobuzCountryValue = "US";
    private const string TidalClientHeader = "x-client";
    private const string TidalClientValue = "BiniLossless/v3.4";
    private static readonly string[] TidalQualityOrder = { "HI_RES_LOSSLESS", "LOSSLESS", "HIGH", "LOW" };
    
    // Quality mappings
    // Qobuz: 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz, 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps
    // Tidal: HI_RES_LOSSLESS (FLAC 24-bit), LOSSLESS (FLAC 16-bit), HIGH (320kbps AAC), LOW (96kbps AAC)
    
    private bool IsQobuzSource => _squidWTFSettings.Source.Equals("Qobuz", StringComparison.OrdinalIgnoreCase);

    protected override string ProviderName => "squidwtf";

    public SquidWTFDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<SquidWTFSettings> squidWTFSettings,
        SquidWTFInstanceManager instanceManager,
        IServiceProvider serviceProvider,
        ILogger<SquidWTFDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _squidWTFSettings = squidWTFSettings.Value;
        _instanceManager = instanceManager;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            // Test connectivity to the appropriate backend
            if (IsQobuzSource)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{QobuzBaseUrl}/api/get-music?q=test&offset=0");
                request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);
                
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            else
            {
                // Test Tidal with instance manager
                using var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/search/?s=test");
                    request.Headers.Add(TidalClientHeader, TidalClientValue);
                    return request;
                });
                return response.IsSuccessStatusCode;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SquidWTF service not available");
            return false;
        }
    }

    public override async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        if (SubsonicSettings.StreamOnly)
        {
            return await StreamWithoutDownloadAsync(externalProvider, externalId, cancellationToken);
        }

        var localPath = await GetLocalPathIfExistsAsync(externalProvider, externalId, cancellationToken);
        if (!string.IsNullOrEmpty(localPath))
        {
            Logger.LogInformation("Streaming locally downloaded track for {TrackId}: {Path}", externalId, localPath);
            return IOFile.OpenRead(localPath);
        }

        // Keep existing behavior for Qobuz source.
        if (IsQobuzSource)
        {
            return await base.DownloadAndStreamAsync(externalProvider, externalId, cancellationToken);
        }

        try
        {
            var requestedQuality = GetTidalQuality();
            var (manifest, actualQuality) = await GetTidalManifestWithFallbackAsync(externalId, requestedQuality, cancellationToken);
            var streamUrl = manifest.Urls![0];

            Logger.LogInformation(
                "Streaming Tidal track {TrackId} directly while background download runs (quality: {Quality})",
                externalId,
                actualQuality);

            StartBackgroundDownloadIfNeeded(externalProvider, externalId);
            return await OpenRemoteStreamAsync(streamUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(
                ex,
                "Direct Tidal streaming failed for track {TrackId}, falling back to download-then-stream",
                externalId);
            return await base.DownloadAndStreamAsync(externalProvider, externalId, cancellationToken);
        }
    }

    protected override async Task<Stream> StreamWithoutDownloadAsync(string externalProvider, string externalId, CancellationToken cancellationToken)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        if (IsQobuzSource)
        {
            var downloadUrl = await GetQobuzDownloadUrlAsync(externalId, cancellationToken);
            Logger.LogInformation("Streaming Qobuz track {TrackId} in StreamOnly mode", externalId);
            return await OpenRemoteStreamAsync(downloadUrl, cancellationToken);
        }

        var requestedQuality = GetTidalQuality();
        var (manifest, actualQuality) = await GetTidalManifestWithFallbackAsync(externalId, requestedQuality, cancellationToken);
        var streamUrl = manifest.Urls![0];

        Logger.LogInformation(
            "Streaming Tidal track {TrackId} in StreamOnly mode (quality: {Quality})",
            externalId,
            actualQuality);

        return await OpenRemoteStreamAsync(streamUrl, cancellationToken);
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-squidwtf-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }

    protected override string? GetTargetQuality()
    {
        if (!string.IsNullOrEmpty(_squidWTFSettings.Quality))
        {
            return _squidWTFSettings.Quality;
        }
        
        // Default to highest quality
        return IsQobuzSource ? "27" : "HI_RES_LOSSLESS";
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        if (IsQobuzSource)
        {
            return await DownloadTrackQobuzAsync(trackId, song, cancellationToken);
        }
        else
        {
            return await DownloadTrackTidalAsync(trackId, song, cancellationToken);
        }
    }

    #endregion

    #region Qobuz Download

    private async Task<DownloadResult> DownloadTrackQobuzAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var quality = GetQobuzQuality();
        var downloadUrl = await GetQobuzDownloadUrlAsync(trackId, cancellationToken);
        Logger.LogInformation("Got download URL for track {TrackId}: {Title}", trackId, song.Title);
        
        // Determine file extension based on quality
        // Qobuz: 27/7/6 = FLAC, 5 = MP3
        var extension = quality == "5" ? ".mp3" : ".flac";
        var downloadedQuality = quality switch
        {
            "27" => "FLAC_24_192",
            "7" => "FLAC_24_96",
            "6" => "FLAC_16",
            "5" => "MP3_320",
            _ => "FLAC"
        };
        
        // Build output path
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);
        
        // Download the file (no decryption needed)
        await DownloadFileAsync(downloadUrl, outputPath, cancellationToken);
        
        // Write metadata
        await WriteMetadataAsync(outputPath, song, cancellationToken);
        
        return new DownloadResult(outputPath, downloadedQuality);
    }

    private async Task<string> GetQobuzDownloadUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var quality = GetQobuzQuality();
        var url = $"{QobuzBaseUrl}/api/download-music?track_id={trackId}&quality={quality}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(QobuzCountryHeader, QobuzCountryValue);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var downloadResponse = JsonSerializer.Deserialize<QobuzDownloadResponse>(json);

        if (downloadResponse?.Success != true || string.IsNullOrEmpty(downloadResponse.Data?.Url))
        {
            throw new Exception("Failed to get download URL from SquidWTF Qobuz");
        }

        return downloadResponse.Data.Url;
    }

    private string GetQobuzQuality()
    {
        var quality = _squidWTFSettings.Quality;
        
        if (string.IsNullOrEmpty(quality))
        {
            return "27"; // Default to highest quality FLAC (24-bit/192kHz)
        }
        
        // Map common quality names to Qobuz quality codes
        // 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz, 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps
        return quality.ToUpperInvariant() switch
        {
            "FLAC_24_192" or "FLAC_24" or "27" => "27",
            "FLAC_24_96" or "7" => "7",
            "FLAC_16" or "FLAC" or "6" => "6",
            "MP3_320" or "MP3" or "5" => "5",
            _ => "27"
        };
    }

    #endregion

    #region Tidal Download

    private async Task<DownloadResult> DownloadTrackTidalAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var requestedQuality = GetTidalQuality();
        var (manifest, actualQuality) = await GetTidalManifestWithFallbackAsync(trackId, requestedQuality, cancellationToken);
        
        var downloadUrl = manifest.Urls[0];
        Logger.LogInformation("Got download URL for track {TrackId}: {Title} (quality: {Quality})", trackId, song.Title, actualQuality);
        
        // Determine file extension based on manifest mime type
        var extension = GetExtensionFromMimeType(manifest.MimeType);
        var downloadedQuality = GetDownloadedQuality(actualQuality, manifest.MimeType);
        
        // Build output path
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);
        
        // Download the file (no decryption needed)
        await DownloadFileAsync(downloadUrl, outputPath, cancellationToken);
        
        // Write metadata
        await WriteMetadataAsync(outputPath, song, cancellationToken);
        
        return new DownloadResult(outputPath, downloadedQuality);
    }

    /// <summary>
    /// Gets the first playable Tidal manifest using descending quality fallback.
    /// Uses instance manager for automatic failover
    /// </summary>
    private async Task<(TidalManifest manifest, string quality)> GetTidalManifestWithFallbackAsync(
        string trackId, string requestedQuality, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var candidateQuality in BuildTidalQualityFallbackChain(requestedQuality))
        {
            try
            {
                var manifest = await GetTidalManifestForQualityAsync(trackId, candidateQuality, cancellationToken);
                if (manifest.Urls == null || manifest.Urls.Count == 0 || string.IsNullOrWhiteSpace(manifest.Urls[0]))
                {
                    throw new Exception("No playable URLs in Tidal manifest");
                }

                return (manifest, candidateQuality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                Logger.LogWarning(ex, "Failed to get Tidal manifest for track {TrackId} at quality {Quality}", trackId, candidateQuality);
            }
        }

        throw new Exception(
            $"Failed to get a playable Tidal manifest for track {trackId} after trying all quality fallbacks",
            lastError);
    }

    private async Task<TidalManifest> GetTidalManifestForQualityAsync(string trackId, string quality, CancellationToken cancellationToken)
    {
        using var response = await _instanceManager.SendWithFailoverAsync(baseUrl =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/track/?id={trackId}&quality={quality}");
            request.Headers.Add(TidalClientHeader, TidalClientValue);
            return request;
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var wrapper = JsonSerializer.Deserialize<TidalTrackDownloadResponseWrapper>(json);
        var trackResponse = wrapper?.Data;

        if (string.IsNullOrWhiteSpace(trackResponse?.Manifest))
        {
            throw new Exception("Missing manifest payload from SquidWTF Tidal");
        }

        var manifestMimeType = trackResponse.ManifestMimeType ?? "";
        if (manifestMimeType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ||
            manifestMimeType.Contains("application/dash", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"Unsupported DASH manifest format ({manifestMimeType})");
        }

        var manifestJson = DecodeManifestJson(trackResponse.Manifest);
        var manifest = JsonSerializer.Deserialize<TidalManifest>(manifestJson);
        if (manifest == null)
        {
            throw new Exception("Failed to deserialize Tidal manifest JSON");
        }

        manifest.MimeType ??= trackResponse.ManifestMimeType;
        return manifest;
    }

    private static List<string> BuildTidalQualityFallbackChain(string requestedQuality)
    {
        var requested = string.IsNullOrWhiteSpace(requestedQuality)
            ? "HI_RES_LOSSLESS"
            : requestedQuality.ToUpperInvariant();
        var chain = new List<string> { requested };

        foreach (var candidate in TidalQualityOrder)
        {
            if (!candidate.Equals(requested, StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(candidate);
            }
        }

        return chain;
    }

    private static string DecodeManifestJson(string rawManifest)
    {
        var trimmed = rawManifest.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Handle URL-safe base64 manifests (replace '-'/'_' and pad).
        var normalized = trimmed
            .Replace('-', '+')
            .Replace('_', '/');

        var remainder = normalized.Length % 4;
        if (remainder == 2)
        {
            normalized += "==";
        }
        else if (remainder == 3)
        {
            normalized += "=";
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException ex)
        {
            throw new Exception("Invalid Tidal manifest encoding", ex);
        }
    }

    private string GetTidalQuality()
    {
        var quality = _squidWTFSettings.Quality;
        
        if (string.IsNullOrEmpty(quality))
        {
            return "HI_RES_LOSSLESS"; // Default to highest quality
        }
        
        // Map common quality names to Tidal quality codes
        return quality.ToUpperInvariant() switch
        {
            "HI_RES_LOSSLESS" or "HI_RES" or "FLAC_24" => "HI_RES_LOSSLESS",
            "LOSSLESS" or "FLAC" or "FLAC_16" => "LOSSLESS",
            "HIGH" or "AAC_320" or "AAC_HIGH" => "HIGH",
            "LOW" or "AAC_96" or "AAC_LOW" => "LOW",
            _ => "HI_RES_LOSSLESS"
        };
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Determines file extension based on the manifest's mime type
    /// </summary>
    private static string GetExtensionFromMimeType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
            return ".mp3";
            
        return mimeType.ToLowerInvariant() switch
        {
            var m when m.Contains("flac") => ".flac",
            var m when m.Contains("mp4") || m.Contains("m4a") || m.Contains("aac") => ".m4a",
            var m when m.Contains("mp3") || m.Contains("mpeg") => ".mp3",
            _ => ".mp3"
        };
    }

    /// <summary>
    /// Determines the quality string for the downloaded file
    /// </summary>
    private static string GetDownloadedQuality(string requestedQuality, string? mimeType)
    {
        if (mimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true)
        {
            return requestedQuality == "HI_RES_LOSSLESS" ? "FLAC_24" : "FLAC_16";
        }
        
        // AAC/M4A from Tidal - determine bitrate based on requested quality
        if (mimeType?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true ||
            mimeType?.Contains("aac", StringComparison.OrdinalIgnoreCase) == true)
        {
            return requestedQuality switch
            {
                "HIGH" => "AAC_320",
                "LOW" => "AAC_96",
                _ => "AAC_320"  // Default if we got AAC but didn't specifically request it
            };
        }
        
        return "MP3_320";
    }

    private async Task DownloadFileAsync(string url, string outputPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");
        
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
        await responseStream.CopyToAsync(outputFile, cancellationToken);
        
        Logger.LogInformation("Downloaded file to: {Path}", outputPath);
    }

    private void StartBackgroundDownloadIfNeeded(string externalProvider, string externalId)
    {
        var songId = $"ext-{externalProvider}-{externalId}";
        if (ActiveDownloads.TryGetValue(songId, out var existing) &&
            existing.Status is DownloadStatus.InProgress or DownloadStatus.Completed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Background download failed for track {TrackId}", externalId);
            }
        });
    }

    private async Task<Stream> OpenRemoteStreamAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Headers.Add("Accept", "*/*");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ResponseOwnedStream(response, responseStream);
    }

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;

        public ResponseOwnedStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.CanSeek ? _inner.Length : 0;
        public override long Position
        {
            get => _inner.CanSeek ? _inner.Position : 0;
            set
            {
                if (!_inner.CanSeek)
                {
                    throw new NotSupportedException();
                }

                _inner.Position = value;
            }
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _response.Dispose();
            await base.DisposeAsync();
        }
    }

    #endregion
}
