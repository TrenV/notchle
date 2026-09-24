using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Notchle.Windows.Island;

/// Album covers for the island, from a disk cache (%LOCALAPPDATA%\Notchle\artwork\&lt;trackId&gt;.jpg)
/// filled on demand from the URL the coordinator resolved. UI thread only. Offline or broken
/// images simply stay null: the views then draw nothing (answer) or a note (history rows).
/// The URL only ever reaches this class from an answer screen or a history row, so a cover is
/// never fetched for a track that is still being guessed.
internal sealed class ArtworkImages
{
    private readonly string? _directory;
    private readonly HttpClient? _http;
    private readonly Func<string, ImageSource?>? _preloaded;
    private readonly Dictionary<string, ImageSource?> _memory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <param name="directory">Disk cache; null keeps nothing on disk.</param>
    /// <param name="http">Downloads missing covers; null never touches the network.</param>
    public ArtworkImages(string? directory, HttpClient? http)
    {
        _directory = directory;
        _http = http;
    }

    private ArtworkImages(Func<string, ImageSource?> preloaded) => _preloaded = preloaded;

    /// No disk, no network: every cover is missing (tests).
    public static ArtworkImages None { get; } = new(null, null);

    /// Covers from memory only, by track id (snapshots, tests).
    public static ArtworkImages FromMemory(Func<string, ImageSource?> images) => new(images);

    /// A cover arrived (download finished): the island redraws.
    public event Action? Loaded;

    /// The cover of <paramref name="trackId"/>, or null (not known, not downloaded yet, offline).
    public ImageSource? For(string trackId, Uri? url)
    {
        if (_preloaded is not null) return url is null ? null : _preloaded(trackId);
        if (url is null || string.IsNullOrEmpty(trackId)) return null;
        if (_memory.TryGetValue(trackId, out var known)) return known;
        if (FilePath(trackId) is { } path && File.Exists(path))
        {
            var image = LoadFile(path);
            _memory[trackId] = image;
            return image;
        }
        StartDownload(trackId, url);
        return null;
    }

    private string? FilePath(string trackId)
    {
        if (_directory is null) return null;
        var safe = string.Concat(trackId.Where(char.IsAsciiLetterOrDigit));
        return safe.Length == 0 ? null : Path.Combine(_directory, safe + ".jpg");
    }

    private void StartDownload(string trackId, Uri url)
    {
        if (_http is null || !_pending.Add(trackId)) return;
        var path = FilePath(trackId);
        _ = Task.Run(async () =>
        {
            byte[]? bytes = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                bytes = await _http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
                if (path is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + $".{Guid.NewGuid():N}.tmp";
                    await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                    File.Move(temp, path, overwrite: true);
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning($"Notchle: album cover download failed: {error.Message}");
            }
            await _dispatcher.InvokeAsync(() =>
            {
                _pending.Remove(trackId);
                if (bytes is null) return; // offline: try again next time it is asked for
                _memory[trackId] = Decode(new MemoryStream(bytes));
                Loaded?.Invoke();
            });
        });
    }

    private static ImageSource? LoadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Decode(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Decoded fully now (OnLoad) and frozen, so the stream can go and any thread may draw it.
    private static ImageSource? Decode(Stream stream)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or IOException or InvalidOperationException
                                          or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
