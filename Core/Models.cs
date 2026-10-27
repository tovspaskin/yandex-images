namespace YandexImages.Core;

public sealed record DiskSource(string PublicKey, string Path)
{
    public static DiskSource Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Введите полную публичную ссылку Яндекс Диска.", nameof(input));

        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.Scheme != Uri.UriSchemeHttps || host is not ("disk.yandex.ru" or "disk.360.yandex.ru" or "disk.yandex.com" or "yadi.sk" or "www.yadi.sk") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new ArgumentException("Нужна публичная HTTPS-ссылка disk.yandex.ru, disk.360.yandex.ru, disk.yandex.com или yadi.sk.", nameof(input));

        // Both forms are accepted by the public API. Keep one stable public key for Yandex 360 links.
        if (host == "disk.360.yandex.ru")
            host = "disk.yandex.ru";

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string kind;
        string token;
        int firstNested;
        if (parts.Length >= 3 && parts[0].Equals("client", StringComparison.OrdinalIgnoreCase) && parts[1] == "aa" &&
            parts[2].StartsWith("d_", StringComparison.Ordinal) && parts[2].Length > 2)
        {
            kind = "d";
            token = Uri.UnescapeDataString(parts[2][2..]);
            firstNested = 3;
            host = "disk.yandex.ru";
        }
        else if (parts.Length >= 2 && parts[0] is "d" or "i")
        {
            kind = parts[0];
            token = Uri.UnescapeDataString(parts[1]);
            firstNested = 2;
        }
        else if (parts.Length > 0 && parts[0].Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Это личная ссылка Яндекс Диска. Откройте доступ к папке и скопируйте публичную ссылку вида https://disk.yandex.ru/d/…", nameof(input));
        }
        else
        {
            throw new ArgumentException("Не удалось найти публичный ключ в ссылке. Используйте ссылку вида https://disk.yandex.ru/d/…", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(token) || token.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ArgumentException("Некорректный публичный ключ Яндекс Диска.", nameof(input));

        var path = string.Join('/', parts.Skip(firstNested).Select(Uri.UnescapeDataString));
        foreach (var query in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = query.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? query : query[..separator]);
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                path = Uri.UnescapeDataString((separator < 0 ? "" : query[(separator + 1)..]).Replace('+', ' '));
                break;
            }
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("Путь в публичной ссылке содержит недопустимый переход между папками.", nameof(input));
        return new DiskSource($"https://{host}/{kind}/{Uri.EscapeDataString(token)}", "/" + string.Join('/', segments));
    }
}

public sealed record RemoteImage(string Name, string Path, string RelativePath, long Size, string? Md5, string? Sha256);

public sealed record DownloadUpdate(int Completed, int Total, int Downloaded, int Skipped, int Failed,
    long BytesReceived, long TotalBytes, string CurrentFile, string Message);

public sealed record DownloadResult(int Downloaded, int Skipped, int Failed, IReadOnlyList<string> Errors);
