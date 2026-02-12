using octo_fiesta.Services.Local;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;

namespace octo_fiesta.Tests;

public class LocalLibraryServiceTests : IDisposable
{
    private readonly LocalLibraryService _service;
    private readonly string _testDownloadPath;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;

    public LocalLibraryServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        // Mock HttpClient
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"scanStatus\":{\"scanning\":false,\"count\":100}}}")
            });
        
        var httpClient = new HttpClient(mockHandler.Object);
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var subsonicSettings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var mockLogger = new Mock<ILogger<LocalLibraryService>>();

        _service = new LocalLibraryService(configuration, _mockHttpClientFactory.Object, subsonicSettings, mockLogger.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public void ParseSongId_WithExternalId_ReturnsCorrectParts()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("ext-deezer-123456");

        // Assert
        Assert.True(isExternal);
        Assert.Equal("deezer", provider);
        Assert.Equal("123456", externalId);
    }

    [Fact]
    public void ParseSongId_WithLocalId_ReturnsNotExternal()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("local-789");

        // Assert
        Assert.False(isExternal);
        Assert.Null(provider);
        Assert.Null(externalId);
    }

    [Fact]
    public void ParseSongId_WithNumericId_ReturnsNotExternal()
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId("12345");

        // Assert
        Assert.False(isExternal);
        Assert.Null(provider);
        Assert.Null(externalId);
    }

    [Fact]
    public async Task GetLocalPathForExternalSongAsync_WhenNotRegistered_ReturnsNull()
    {
        // Act
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterDownloadedSongAsync_ThenGetLocalPath_ReturnsPath()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-deezer-123456",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            ExternalProvider = "deezer",
            ExternalId = "123456"
        };
        var localPath = Path.Combine(_testDownloadPath, "test-song.mp3");
        
        // Create the file
        await File.WriteAllTextAsync(localPath, "fake audio content");

        // Act
        await _service.RegisterDownloadedSongAsync(song, localPath);
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "123456");

        // Assert
        Assert.Equal(localPath, result);
    }

    [Fact]
    public async Task GetLocalPathForExternalSongAsync_WhenFileDeleted_ReturnsNull()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-deezer-999999",
            Title = "Deleted Song",
            Artist = "Test Artist",
            Album = "Test Album",
            ExternalProvider = "deezer",
            ExternalId = "999999"
        };
        var localPath = Path.Combine(_testDownloadPath, "deleted-song.mp3");
        
        // Create and then delete the file
        await File.WriteAllTextAsync(localPath, "fake audio content");
        await _service.RegisterDownloadedSongAsync(song, localPath);
        File.Delete(localPath);

        // Act
        var result = await _service.GetLocalPathForExternalSongAsync("deezer", "999999");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterDownloadedSongAsync_WithNullProvider_DoesNothing()
    {
        // Arrange
        var song = new Song
        {
            Id = "local-123",
            Title = "Local Song",
            Artist = "Local Artist",
            Album = "Local Album",
            ExternalProvider = null,
            ExternalId = null
        };
        var localPath = Path.Combine(_testDownloadPath, "local-song.mp3");

        // Act - should not throw
        await _service.RegisterDownloadedSongAsync(song, localPath);

        // Assert - nothing to assert, just checking it doesn't throw
        Assert.True(true);
    }

    [Fact]
    public async Task TriggerLibraryScanAsync_ReturnsTrue()
    {
        // Act
        var result = await _service.TriggerLibraryScanAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetScanStatusAsync_ReturnsScanStatus()
    {
        // Act
        var result = await _service.GetScanStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Scanning);
        Assert.Equal(100, result.Count);
    }

    [Theory]
    [InlineData("ext-deezer-123", true, "deezer", "123")]
    [InlineData("ext-spotify-abc123", true, "spotify", "abc123")]
    [InlineData("ext-tidal-999-888", true, "tidal", "999-888")]
    [InlineData("ext-deezer-song-123456", true, "deezer", "123456")]  // New format - extracts numeric ID
    [InlineData("ext-SquidWTF-Song-345949499", true, "squidwtf", "345949499")] // Mixed case provider/type
    [InlineData("track://ext-squidwtf-song-345949499", true, "squidwtf", "345949499")] // URI-wrapped format
    [InlineData("track://ext-squidwtf-song-345949499?source=ma", true, "squidwtf", "345949499")] // URI-wrapped format with query
    [InlineData("track%3A%2F%2Fext-squidwtf-song-345949499", true, "squidwtf", "345949499")] // URL-encoded URI format
    [InlineData("album://ext-squidwtf-album-br7ad0ufcms0b", true, "squidwtf", "br7ad0ufcms0b")] // URI-wrapped album format
    [InlineData("track://opensubsonic--zvFBBaob/ext-squidwtf-song-345949499", true, "squidwtf", "345949499")] // URI with provider scope
    [InlineData("123456", false, null, null)]
    [InlineData("", false, null, null)]
    [InlineData("ext-", false, null, null)]
    [InlineData("ext-deezer", false, null, null)]
    public void ParseSongId_VariousInputs_ReturnsExpected(string songId, bool expectedIsExternal, string? expectedProvider, string? expectedExternalId)
    {
        // Act
        var (isExternal, provider, externalId) = _service.ParseSongId(songId);

        // Assert
        Assert.Equal(expectedIsExternal, isExternal);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedExternalId, externalId);
    }

    [Theory]
    [InlineData("ext-deezer-song-123456", true, "deezer", "song", "123456")]
    [InlineData("ext-deezer-album-789012", true, "deezer", "album", "789012")]
    [InlineData("ext-deezer-artist-259", true, "deezer", "artist", "259")]
    [InlineData("ext-spotify-song-abc123", true, "spotify", "song", "abc123")]
    [InlineData("ext-SquidWTF-Song-345949499", true, "squidwtf", "song", "345949499")] // Mixed case provider/type
    [InlineData("track://ext-squidwtf-song-345949499", true, "squidwtf", "song", "345949499")] // URI-wrapped format
    [InlineData("track://ext-squidwtf-song-345949499?source=ma", true, "squidwtf", "song", "345949499")] // URI-wrapped format with query
    [InlineData("track%3A%2F%2Fext-squidwtf-song-345949499", true, "squidwtf", "song", "345949499")] // URL-encoded URI format
    [InlineData("album://ext-squidwtf-album-br7ad0ufcms0b", true, "squidwtf", "album", "br7ad0ufcms0b")] // URI-wrapped album format
    [InlineData("track://opensubsonic--zvFBBaob/ext-squidwtf-song-345949499", true, "squidwtf", "song", "345949499")] // URI with provider scope
    [InlineData("ext-deezer-123", true, "deezer", "song", "123")]  // Legacy format defaults to song
    [InlineData("ext-tidal-999", true, "tidal", "song", "999")]    // Legacy format defaults to song
    [InlineData("123456", false, null, null, null)]
    [InlineData("", false, null, null, null)]
    [InlineData("ext-", false, null, null, null)]
    [InlineData("ext-deezer", false, null, null, null)]
    public void ParseExternalId_VariousInputs_ReturnsExpected(string id, bool expectedIsExternal, string? expectedProvider, string? expectedType, string? expectedExternalId)
    {
        // Act
        var (isExternal, provider, type, externalId) = _service.ParseExternalId(id);

        // Assert
        Assert.Equal(expectedIsExternal, isExternal);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedExternalId, externalId);
    }

    [Fact]
    public async Task RemoveDuplicateTracksInAlbumFolderAsync_WhenPreferredDuplicateExists_KeepsPreferredAndDeletesOthers()
    {
        // Arrange
        var albumFolder = Path.Combine(_testDownloadPath, "Artist", "Album");
        Directory.CreateDirectory(albumFolder);

        var originalPath = Path.Combine(albumFolder, "01 - Test Song.mp3");
        var duplicatePath = Path.Combine(albumFolder, "01 - Test Song (1).mp3");
        var originalLyrics = Path.ChangeExtension(originalPath, ".lrc");
        var duplicateLyrics = Path.ChangeExtension(duplicatePath, ".lrc");

        await File.WriteAllTextAsync(originalPath, "old-version");
        await File.WriteAllTextAsync(duplicatePath, "new-version");
        await File.WriteAllTextAsync(originalLyrics, "old-lyrics");
        await File.WriteAllTextAsync(duplicateLyrics, "new-lyrics");

        // Act
        var deletedCount = await _service.RemoveDuplicateTracksInAlbumFolderAsync(duplicatePath);

        // Assert
        Assert.Equal(1, deletedCount);
        Assert.False(File.Exists(originalPath));
        Assert.False(File.Exists(originalLyrics));
        Assert.True(File.Exists(duplicatePath));
        Assert.True(File.Exists(duplicateLyrics));
    }

    [Fact]
    public async Task RemoveDuplicateTracksInAlbumFolderAsync_WithMp3AndFlac_KeepsPreferredFlac()
    {
        // Arrange
        var albumFolder = Path.Combine(_testDownloadPath, "Artist2", "Album2");
        Directory.CreateDirectory(albumFolder);

        var mp3Path = Path.Combine(albumFolder, "02 - Test Track.mp3");
        var flacPath = Path.Combine(albumFolder, "02 - Test Track.flac");

        await File.WriteAllTextAsync(mp3Path, "mp3");
        await File.WriteAllTextAsync(flacPath, "flac-quality");

        // Act
        var deletedCount = await _service.RemoveDuplicateTracksInAlbumFolderAsync(flacPath);

        // Assert
        Assert.Equal(1, deletedCount);
        Assert.False(File.Exists(mp3Path));
        Assert.True(File.Exists(flacPath));
    }
}
