using System.Globalization;
using System.Net;
using System.Text.Json;

namespace YandexImages.Core;

public sealed class YandexDiskClient : IDisposable
{
    private const string ApiRoot = "https://cloud-api.yandex.net/v1/disk/public/resources";
    private const int PageSize = 200;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".bmp", ".dib", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".raw", ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf",
        ".sr2", ".orf", ".rw2", ".raf", ".pef", ".x3f", ".rwl", ".srw", ".ico", ".svg", ".psd", ".jxl"
    };
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public YandexDiskClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<IReadOnlyList<RemoteImage>> ListImagesAsync(DiskSource source, bool recursive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var images = new List<RemoteImage>();
        var directories = new Queue<(string Path, string Relative)>();
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        directories.Enqueue((NormalizeRemotePath(source.Path), ""));
        while (directories.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedDirectories.Add(directory.Path))
                continue;
            var offset = 0;
            while (true)
            {
                using var document = await GetJsonAsync(ResourceUrl(source, directory.Path, offset), cancellationToken).ConfigureAwait(false);
                var resource = document.RootElement;
                var type = GetString(resource, "type");
                if (type == "file")
                {
                    AddImage(resource, directory.Relative.Length == 0 ? GetString(resource, "name") ?? "image" : directory.Relative);
                    break;
                }
                if (type != "dir")
                    throw new InvalidDataException("Яндекс Диск вернул ресурс неизвестного типа.");
                if (!resource.TryGetProperty("_embedded", out var embedded) ||
                    !embedded.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("В ответе Яндекс Диска отсутствует список файлов.");

                var count = 0;
                foreach (var item in items.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                    var name = GetString(item, "name") ?? throw new InvalidDataException("В ответе отсутствует имя файла.");
                    var path = GetString(item, "path") ?? JoinRemote(directory.Path, name);
                    var relative = directory.Relative.Length == 0 ? name : directory.Relative + "/" + name;
                    if (GetString(item, "type") == "dir")
                    {
                        if (recursive)
                            directories.Enqueue((path, relative));
                    }
                    else if (GetString(item, "type") == "file")
                        AddImage(item, relative);
                }

                offset = checked(offset + count);
                var total = embedded.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt64(out var totalValue)
                    ? totalValue : (long?)null;
                if (count == 0 || (total.HasValue ? offset >= total.Value : count < PageSize))
                    break;
            }
        }
        return images;

        void AddImage(JsonElement item, string relative)
        {
            var name = GetString(item, "name") ?? "image";
            var path = GetString(item, "path") ?? NormalizeRemotePath(source.Path);
            if (!IsImage(item, name) || !seenFiles.Add(path))
                return;
            var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : -1;
            images.Add(new RemoteImage(name, path, relative, size, GetString(item, "md5"), GetString(item, "sha256")));
        }
    }

    public async Task<string> GetDownloadUrlAsync(DiskSource source, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = ApiRoot + "/download?public_key=" + Uri.EscapeDataString(source.PublicKey) + "&path=" + Uri.EscapeDataString(path);
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var href = GetString(document.RootElement, "href");
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Яндекс Диск не вернул HTTPS-ссылку на оригинал файла.");
        return uri.AbsoluteUri;
    }

    internal async Task<HttpResponseMessage> SendDownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var retryDelay = GetRetryDelay(response);
            var statusCode = response.StatusCode;
            response.Dispose();
            if (IsTransient(statusCode))
                throw new RetryableDownloadException($"Сервер загрузки вернул HTTP {(int)statusCode}.", retryDelay);
            throw new HttpRequestException($"Сервер загрузки вернул HTTP {(int)statusCode}.", null, statusCode);
        }
        return response;
    }

    internal static bool ShouldRetry(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is not InvalidDataException &&
            exception is RetryableDownloadException or IOException or HttpRequestException { StatusCode: null } or OperationCanceledException;

    internal static Task WaitBeforeRetryAsync(int attempt, Exception exception, CancellationToken cancellationToken)
    {
        var delay = exception is RetryableDownloadException retryable && retryable.RetryDelay.HasValue
            ? retryable.RetryDelay.Value : TimeSpan.FromMilliseconds(400 * (1 << attempt));
        return Task.Delay(delay, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode))
                        throw new RetryableDownloadException($"Яндекс Диск вернул HTTP {(int)response.StatusCode}.", GetRetryDelay(response));
                    var message = response.StatusCode switch
                    {
                        HttpStatusCode.NotFound => "Публичная ссылка или папка не найдена. Проверьте ссылку и доступ к ней.",
                        HttpStatusCode.Forbidden => "Яндекс Диск запретил доступ. Проверьте, опубликована ли папка и разрешено ли скачивание.",
                        _ => $"Яндекс Диск вернул HTTP {(int)response.StatusCode}."
                    };
                    throw new HttpRequestException(message, null, response.StatusCode);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 2 && ShouldRetry(exception, cancellationToken))
            {
                await WaitBeforeRetryAsync(attempt, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ResourceUrl(DiskSource source, string path, int offset) =>
        ApiRoot + "?public_key=" + Uri.EscapeDataString(source.PublicKey) + "&path=" + Uri.EscapeDataString(path) +
        "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&offset=" + offset.ToString(CultureInfo.InvariantCulture) + "&sort=name";

    private static bool IsImage(JsonElement item, string name) =>
        string.Equals(GetString(item, "media_type"), "image", StringComparison.OrdinalIgnoreCase) ||
        (GetString(item, "mime_type")?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) ||
        ImageExtensions.Contains(System.IO.Path.GetExtension(name));

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode == (HttpStatusCode)429 || (int)statusCode >= 500;

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date.HasValue == true ? retry.Date.Value - DateTimeOffset.UtcNow : null);
        return delay.HasValue ? TimeSpan.FromMilliseconds(Math.Clamp(delay.Value.TotalMilliseconds, 0, 30_000)) : null;
    }

    private static string NormalizeRemotePath(string path) => string.IsNullOrWhiteSpace(path) || path == "/" ? "/" : "/" + path.Trim('/');
    private static string JoinRemote(string path, string name) => path.TrimEnd('/') + "/" + name;

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}

internal sealed class RetryableDownloadException(string message, TimeSpan? retryDelay) : Exception(message)
{
    internal TimeSpan? RetryDelay { get; } = retryDelay;
}
