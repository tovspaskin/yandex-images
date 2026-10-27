using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace YandexImages.Core;

public sealed class ImageDownloader(YandexDiskClient client)
{
    private readonly YandexDiskClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<DownloadResult> DownloadAsync(DiskSource source, IReadOnlyList<RemoteImage> images,
        string destination, int parallelism, IProgress<DownloadUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var root = System.IO.Path.GetFullPath(destination);
        SafePaths.EnsureDirectory(root, root);
        var plan = SafePaths.Plan(root, images);
        var errors = new ConcurrentQueue<string>();
        var completed = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        long bytesReceived = 0;
        var totalBytes = images.Aggregate(0L, (total, image) => SaturatingAdd(total, Math.Max(0, image.Size)));
        var progressGate = new object();
        var lastByteReport = Stopwatch.GetTimestamp();
        var claimedPaths = new HashSet<string>(plan.Select(item => item.Target), StringComparer.OrdinalIgnoreCase);
        var pathGate = new object();
        Report("", "Подготовка загрузки");

        await Parallel.ForEachAsync(plan, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(parallelism, 1, 16),
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            try
            {
                var parent = System.IO.Path.GetDirectoryName(item.Target)!;
                SafePaths.EnsureDirectory(root, parent);
                var target = item.Target;
                var suffix = 2;
                var existingMatch = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    SafePaths.CheckFile(root, target);
                    if (await IsVerifiedExistingAsync(target, item.Image, token).ConfigureAwait(false))
                    {
                        existingMatch = true;
                        break;
                    }
                    if (!File.Exists(target) && !Directory.Exists(target))
                        break;
                    lock (pathGate)
                    {
                        do { target = SafePaths.AddSuffix(item.Target, suffix++); }
                        while (!claimedPaths.Add(target));
                    }
                }
                if (existingMatch)
                {
                    Interlocked.Increment(ref skipped);
                    Interlocked.Increment(ref completed);
                    Report(item.Image.RelativePath, "Уже загружен и проверен");
                    return;
                }

                var temporary = System.IO.Path.Combine(parent, ".yandex-" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            SafePaths.EnsureDirectory(root, parent);
                            var url = await _client.GetDownloadUrlAsync(source, item.Image.Path, token).ConfigureAwait(false);
                            using var response = await _client.SendDownloadAsync(url, token).ConfigureAwait(false);
                            await WriteVerifiedAsync(response, temporary, item.Image, token, count =>
                            {
                                Interlocked.Add(ref bytesReceived, count);
                                Report(item.Image.RelativePath, "Загрузка");
                            }).ConfigureAwait(false);
                            break;
                        }
                        catch (Exception exception) when (attempt < 2 && YandexDiskClient.ShouldRetry(exception, token))
                        {
                            SafePaths.DeleteTemporary(root, temporary);
                            Report(item.Image.RelativePath, $"Повтор загрузки: {attempt + 2}/3");
                            await YandexDiskClient.WaitBeforeRetryAsync(attempt, exception, token).ConfigureAwait(false);
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        SafePaths.EnsureDirectory(root, parent);
                        SafePaths.CheckFile(root, target);
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            lock (pathGate)
                            {
                                do { target = SafePaths.AddSuffix(item.Target, suffix++); }
                                while (!claimedPaths.Add(target));
                            }
                            continue;
                        }
                        try
                        {
                            File.Move(temporary, target, overwrite: false);
                            break;
                        }
                        catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                        {
                            // Another process created this name after the check. Reserve another without overwriting it.
                        }
                    }
                }
                finally
                {
                    SafePaths.DeleteTemporary(root, temporary);
                }
                Interlocked.Increment(ref downloaded);
                Interlocked.Increment(ref completed);
                Report(item.Image.RelativePath, "Загружен и проверен");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Enqueue($"{item.Image.RelativePath}: {exception.Message}");
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref completed);
                Report(item.Image.RelativePath, "Ошибка: " + exception.Message);
            }
        }).ConfigureAwait(false);

        Report("", "Загрузка завершена");
        return new DownloadResult(downloaded, skipped, failed, errors.ToArray());

        void Report(string file, string message)
        {
            if (progress is null)
                return;
            lock (progressGate)
            {
                if (message == "Загрузка")
                {
                    var now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(lastByteReport, now) < TimeSpan.FromMilliseconds(100))
                        return;
                    lastByteReport = now;
                }
                progress.Report(new DownloadUpdate(Volatile.Read(ref completed), images.Count, Volatile.Read(ref downloaded),
                    Volatile.Read(ref skipped), Volatile.Read(ref failed), Interlocked.Read(ref bytesReceived), totalBytes, file, message));
            }
        }
    }

    private static async Task<bool> IsVerifiedExistingAsync(string target, RemoteImage image, CancellationToken cancellationToken)
    {
        if (!File.Exists(target) || image.Size < 0 || new FileInfo(target).Length != image.Size)
            return false;
        if (string.IsNullOrWhiteSpace(image.Md5) && string.IsNullOrWhiteSpace(image.Sha256))
            return true;
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = string.IsNullOrWhiteSpace(image.Md5) ? null : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = string.IsNullOrWhiteSpace(image.Sha256) ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        long length = 0;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            length += read;
            md5?.AppendData(buffer, 0, read);
            sha?.AppendData(buffer, 0, read);
        }
        return length == image.Size && HashMatches(md5, image.Md5) && HashMatches(sha, image.Sha256);
    }

    private static async Task WriteVerifiedAsync(HttpResponseMessage response, string temporary, RemoteImage image,
        CancellationToken cancellationToken, Action<int> received)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (image.Size >= 0 && contentLength.HasValue && contentLength.Value != image.Size)
            throw new InvalidDataException($"Размер ответа {contentLength.Value} не совпадает с ожидаемым {image.Size}.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = string.IsNullOrWhiteSpace(image.Md5) ? null : IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = string.IsNullOrWhiteSpace(image.Sha256) ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[128 * 1024];
        long size = 0;
        while (true)
        {
            inactivity.CancelAfter(TimeSpan.FromSeconds(60));
            int count;
            try { count = await source.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Сервер не передавал данные более 60 секунд.", exception);
            }
            if (count == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            md5?.AppendData(buffer, 0, count);
            sha?.AppendData(buffer, 0, count);
            size += count;
            received(count);
            if (image.Size >= 0 && size > image.Size)
                throw new InvalidDataException("Получено больше данных, чем указано в размере оригинала.");
        }
        if ((image.Size >= 0 && size != image.Size) || (contentLength.HasValue && size != contentLength.Value))
            throw new InvalidDataException($"Размер скачанного файла ({size}) не совпадает с ожидаемым ({image.Size}).");
        if (!HashMatches(md5, image.Md5) || !HashMatches(sha, image.Sha256))
            throw new InvalidDataException("Контрольная сумма скачанного файла не совпадает с оригиналом.");
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
    }

    private static bool HashMatches(IncrementalHash? hash, string? expected) => hash is null ||
        string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static long SaturatingAdd(long left, long right) => long.MaxValue - left < right ? long.MaxValue : left + right;
}

internal static class SafePaths
{
    internal sealed record PlannedImage(RemoteImage Image, string Target);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    internal static IReadOnlyList<PlannedImage> Plan(string root, IReadOnlyList<RemoteImage> images)
    {
        var used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = root };
        var paths = images.Select(image => (Image: image, Parts: SplitRelative(image.RelativePath))).ToArray();
        foreach (var item in paths)
        {
            var remoteParent = "";
            for (var index = 0; index < item.Parts.Length - 1; index++)
            {
                var remote = remoteParent.Length == 0 ? item.Parts[index] : remoteParent + "/" + item.Parts[index];
                if (!directories.ContainsKey(remote))
                {
                    var localParent = directories[remoteParent];
                    var component = Reserve(localParent, item.Parts[index], directory: true);
                    directories[remote] = System.IO.Path.Combine(localParent, component);
                }
                remoteParent = remote;
            }
        }
        var result = new List<PlannedImage>(images.Count);
        foreach (var item in paths)
        {
            var remoteParent = string.Join('/', item.Parts.Take(item.Parts.Length - 1));
            var localParent = directories[remoteParent];
            var name = Reserve(localParent, item.Parts[^1], directory: false);
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(localParent, name));
            RequireWithinRoot(root, target);
            result.Add(new PlannedImage(item.Image, target));
        }
        return result;

        string Reserve(string parent, string original, bool directory)
        {
            if (!used.TryGetValue(parent, out var names))
                used[parent] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var name = Sanitize(original);
            var proposed = name;
            var suffix = 2;
            while (!names.Add(proposed) || (directory && File.Exists(System.IO.Path.Combine(parent, proposed))))
                proposed = System.IO.Path.GetFileName(AddSuffix(name, suffix++));
            return proposed;
        }
    }

    private static string[] SplitRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("Недопустимый относительный путь файла.");
        var parts = relative.Split('/');
        if (parts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".."))
            throw new InvalidDataException("Путь файла содержит недопустимый переход между папками.");
        return parts;
    }

    private static string Sanitize(string component)
    {
        var builder = new StringBuilder(component.Length);
        foreach (var value in component)
            builder.Append(value < 32 || "<>:\"/\\|?*".Contains(value) || char.IsSurrogate(value) ? '_' : value);
        var result = builder.ToString().TrimEnd(' ', '.');
        if (result.Length == 0 || result is "." or "..")
            result = "_";
        if (ReservedNames.Contains(result.Split('.')[0].TrimEnd(' ')))
            result = "_" + result;
        if (result.Length > 180)
        {
            var extension = System.IO.Path.GetExtension(result);
            if (extension.Length > 24)
                extension = "";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(component)))[..10];
            result = result[..(168 - extension.Length)] + "_" + hash + extension;
        }
        return result;
    }

    internal static string AddSuffix(string path, int suffix)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        var extension = System.IO.Path.GetExtension(path);
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var name = $"{stem} ({suffix}){extension}";
        return string.IsNullOrEmpty(directory) ? name : System.IO.Path.Combine(directory, name);
    }

    internal static void EnsureDirectory(string root, string directory)
    {
        RequireWithinRoot(root, directory, allowRoot: true);
        var absolute = System.IO.Path.GetFullPath(directory);
        var volume = System.IO.Path.GetPathRoot(absolute)!;
        var current = volume;
        RejectReparse(current);
        foreach (var part in absolute[volume.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            RejectReparse(current);
            if (!Directory.Exists(current))
                Directory.CreateDirectory(current);
            RejectReparse(current);
        }
    }

    internal static void CheckFile(string root, string path)
    {
        RequireWithinRoot(root, path);
        RejectReparse(path);
    }

    internal static void DeleteTemporary(string root, string path)
    {
        try
        {
            // Do not follow a parent replaced by a junction while a transfer was running.
            var parent = System.IO.Path.GetDirectoryName(path)!;
            RequireWithinRoot(root, path);
            CheckExistingAncestors(parent);
            RejectReparse(path);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CheckExistingAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            RejectReparse(current.FullName);
    }

    private static void RejectReparse(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка назначения содержит символическую ссылку или junction: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void RequireWithinRoot(string root, string path, bool allowRoot = false)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var fullPath = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        // A differently cased directory can be a separate sibling on Unix filesystems.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((allowRoot && string.Equals(fullRoot, fullPath, comparison)) ||
            fullPath.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, comparison))
            return;
        throw new IOException("Путь сохранения выходит за пределы выбранной папки.");
    }
}
