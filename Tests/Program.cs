using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YandexImages.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("public links and one-time decoding", ParseSources),
    ("Yandex 360 nested Unicode source and API encoding", Yandex360Source),
    ("invalid and private links", InvalidSources),
    ("pagination, filtering and recursion", Pagination),
    ("single public image", SingleImage),
    ("API transient retry and permanent error", ApiRetry),
    ("list cancellation", ListCancellation),
    ("original download and verified existing skip", DownloadAndSkip),
    ("existing different files are preserved", OverwriteSafety),
    ("case and sanitized filename collisions", NamingCollisions),
    ("case and sanitized directory collisions", DirectoryCollisions),
    ("download transient and body transport retries", DownloadRetry),
    ("corrupt content rejected without final file", Corruption),
    ("download cancellation cleans partial file", DownloadCancellation),
    ("traversal rejected", Traversal),
    ("symlink or junction destination blocked", JunctionProtection)
};
var failures = new List<string>();
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failures.Add(test.Name + ": " + exception); Console.Error.WriteLine("FAIL " + test.Name + ": " + exception.Message); }
}
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} smoke tests passed.");
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = 1;
}

static Task ParseSources()
{
    var source = DiskSource.Parse("https://disk.yandex.ru/client/aa/d_TOKEN/Folder%20one/100%25/%D1%84%D0%BE%D1%82%D0%BE");
    Equal("https://disk.yandex.ru/d/TOKEN", source.PublicKey);
    Equal("/Folder one/100%/фото", source.Path);
    Equal("/%2F/one%", DiskSource.Parse("https://disk.yandex.com/d/ABC?path=%2F%252F%2Fone%25").Path);
    Equal("/", DiskSource.Parse("https://yadi.sk/i/ABC").Path);
    Equal("https://yadi.sk/i/ABC", DiskSource.Parse("https://yadi.sk/i/ABC").PublicKey);
    Equal("/a+b", DiskSource.Parse("https://disk.yandex.ru/d/ABC/a%2Bb").Path);
    return Task.CompletedTask;
}

static async Task InvalidSources()
{
    foreach (var input in new[] { "", "abc", "http://disk.yandex.ru/d/ABC", "https://disk.yandex.ru.attacker.invalid/d/ABC",
                 "https://disk.360.yandex.ru.attacker.invalid/d/ABC", "https://attacker.disk.360.yandex.ru/d/ABC",
                 "https://disk.360.yandex.com/d/ABC", "https://disk.360.yandex.ru@attacker.invalid/d/ABC",
                 "https://disk.yandex.ru/client/disk/Photos", "https://disk.yandex.ru/client/other/d_ABC",
                 "https://disk.yandex.ru/d/A%2FB", "https://user@disk.yandex.ru/d/ABC",
                 "https://disk.yandex.ru/d/ABC?path=%2F..%2Fsecret" })
        await Throws<ArgumentException>(() => Task.FromResult(DiskSource.Parse(input)));
    try { DiskSource.Parse("https://disk.yandex.ru/client/disk"); }
    catch (ArgumentException exception) { True(exception.Message.Contains("публичную"), "Private link error explains public sharing."); }
}

static async Task Yandex360Source()
{
    const string sourceUrl = "https://disk.360.yandex.ru/d/TOKEN/Photos_(Room2)_11%3A30-13%3A00_%D0%9A%D0%BE%D0%BC%D0%B0%D0%BD%D0%B4%D0%B0/JPEG";
    const string expectedPath = "/Photos_(Room2)_11:30-13:00_Команда/JPEG";
    var source = DiskSource.Parse(sourceUrl);
    Equal("https://disk.yandex.ru/d/TOKEN", source.PublicKey);
    Equal(expectedPath, source.Path);
    Equal("/", DiskSource.Parse("https://disk.360.yandex.ru/i/TOKEN").Path);
    Equal("https://disk.yandex.ru/i/TOKEN", DiskSource.Parse("https://disk.360.yandex.ru/i/TOKEN").PublicKey);
    Equal("/literal%3A/100%", DiskSource.Parse("https://disk.360.yandex.ru/d/TOKEN/literal%253A/100%25").Path);
    using var http = new HttpClient(new FakeHandler((request, _) =>
    {
        var query = Query(request.RequestUri!);
        Equal(source.PublicKey, query["public_key"]);
        Equal(expectedPath, query["path"]);
        True(!request.RequestUri!.Query.Contains("%253A", StringComparison.OrdinalIgnoreCase), "Nested colons must be encoded once.");
        return Task.FromResult(Json(new { type = "dir", _embedded = new { total = 0, items = Array.Empty<object>() } }));
    }));
    using var client = new YandexDiskClient(http);
    Equal(0, (await client.ListImagesAsync(source, recursive: true, default)).Count);
}

static async Task Pagination()
{
    var requests = new List<string>();
    using var http = new HttpClient(new FakeHandler((request, _) =>
    {
        var query = Query(request.RequestUri!);
        requests.Add(query["path"] + ":" + query["offset"]);
        Equal("200", query["limit"]);
        Equal("name", query["sort"]);
        Equal("/100%", Query(new Uri("https://example.invalid/?path=%2F100%25"))["path"]);
        if (query["path"] == "/root/child")
            return Task.FromResult(Json(new { type = "dir", _embedded = new { total = 1, items = new[] { FileItem("nested.heic", "/root/child/nested.heic") } } }));
        if (query["offset"] == "0")
        {
            var items = new List<object>();
            for (var index = 0; index < 100; index++) items.Add(FileItem($"image{index:D3}.JPG", $"/root/image{index:D3}.JPG"));
            for (var index = 0; index < 99; index++) items.Add(FileItem($"text{index:D3}.txt", $"/root/text{index:D3}.txt"));
            items.Add(new { type = "dir", name = "child", path = "/root/child" });
            return Task.FromResult(Json(new { type = "dir", _embedded = new { total = 205, items } }));
        }
        Equal("200", query["offset"]);
        return Task.FromResult(Json(new { type = "dir", _embedded = new { total = 205, items = new object[] {
            FileItem("01.raw", "/root/01.raw"), FileItem("02.bin", "/root/02.bin", "image"),
            FileItem("03.dat", "/root/03.dat", mime: "image/png"), FileItem("04.png", "/root/04.png"), FileItem("05.webp", "/root/05.webp") } } }));
    }));
    using var client = new YandexDiskClient(http);
    var images = await client.ListImagesAsync(new DiskSource("https://disk.yandex.ru/d/A", "/root"), true, default);
    Equal(106, images.Count);
    True(images.Any(image => image.RelativePath == "child/nested.heic"));
    Equal(3, requests.Count);
    requests.Clear();
    Equal(105, (await client.ListImagesAsync(new DiskSource("https://disk.yandex.ru/d/A", "/root"), false, default)).Count);
    Equal(2, requests.Count);
}

static async Task SingleImage()
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json(FileItem("photo.png", "/photo.png", size: 42)))));
    using var client = new YandexDiskClient(http);
    var result = await client.ListImagesAsync(Source(), true, default);
    Equal(1, result.Count);
    Equal("photo.png", result[0].RelativePath);
    Equal(42L, result[0].Size);
}

static async Task ApiRetry()
{
    var calls = 0;
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(++calls < 3 ? Transient((HttpStatusCode)429) : Json(new { href = "https://download.invalid/original" }))));
    using var client = new YandexDiskClient(http);
    Equal("https://download.invalid/original", await client.GetDownloadUrlAsync(Source(), "/photo.png", default));
    Equal(3, calls);
    using var forbiddenHttp = new HttpClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
    using var forbidden = new YandexDiskClient(forbiddenHttp);
    calls = 0;
    await Throws<HttpRequestException>(() => forbidden.GetDownloadUrlAsync(Source(), "/a", default));
    Equal(1, calls);
}

static async Task ListCancellation()
{
    using var http = new HttpClient(new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(new { }); }));
    using var client = new YandexDiskClient(http);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    await Throws<OperationCanceledException>(() => client.ListImagesAsync(Source(), true, cancellation.Token));
}

static async Task DownloadAndSkip()
{
    using var directory = new TestDirectory();
    var bytes = Encoding.UTF8.GetBytes("original image bytes");
    var dataCalls = 0;
    using var http = DownloadHttp(bytes, () => dataCalls++);
    using var client = new YandexDiskClient(http);
    var downloader = new ImageDownloader(client);
    var images = new[] { Image("photo.png", "folder/photo.png", bytes) };
    var updates = new ConcurrentQueue<DownloadUpdate>();
    var result = await downloader.DownloadAsync(Source(), images, directory.Path, 3, new InlineProgress<DownloadUpdate>(updates.Enqueue), default);
    True(result.Downloaded == 1, string.Join("; ", result.Errors));
    True(File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "folder", "photo.png")).SequenceEqual(bytes));
    var final = updates.Last();
    Equal(1, final.Completed);
    Equal((long)bytes.Length, final.BytesReceived);
    result = await downloader.DownloadAsync(Source(), images, directory.Path, 3, null, default);
    Equal(1, result.Skipped);
    Equal(1, dataCalls);
    True(!Directory.EnumerateFiles(directory.Path, "*.part", SearchOption.AllDirectories).Any());
}

static async Task OverwriteSafety()
{
    using var directory = new TestDirectory();
    var bytes = Encoding.UTF8.GetBytes("new image");
    var existing = Encoding.UTF8.GetBytes("old image"); // Same size, different hashes.
    await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, "photo.png"), existing);
    await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "photo (2).png"), "untouched");
    using var http = DownloadHttp(bytes);
    using var client = new YandexDiskClient(http);
    var result = await new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("photo.png", "photo.png", bytes) }, directory.Path, 1, null, default);
    Equal(1, result.Downloaded);
    True(File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "photo.png")).SequenceEqual(existing));
    Equal("untouched", File.ReadAllText(System.IO.Path.Combine(directory.Path, "photo (2).png")));
    True(File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "photo (3).png")).SequenceEqual(bytes));
    result = await new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("photo.png", "photo.png", bytes) }, directory.Path, 1, null, default);
    Equal(1, result.Skipped);
    Equal(3, Directory.GetFiles(directory.Path).Length);
}

static async Task NamingCollisions()
{
    using var directory = new TestDirectory();
    var bytes = Encoding.UTF8.GetBytes("image");
    var names = new[] { "a?b.jpg", "a*b.jpg", "Photo.JPG", "photo.jpg", "CON.jpg", "NUL", "trailing. ", "back\\slash.png" };
    using var http = DownloadHttp(bytes);
    using var client = new YandexDiskClient(http);
    var result = await new ImageDownloader(client).DownloadAsync(Source(), names.Select(name => Image(name, name, bytes)).ToArray(), directory.Path, 8, null, default);
    Equal(names.Length, result.Downloaded);
    var saved = Directory.GetFiles(directory.Path).Select(System.IO.Path.GetFileName).ToArray();
    Equal(names.Length, saved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    True(saved.Contains("a_b.jpg") && saved.Contains("a_b (2).jpg"));
    True(saved.Contains("_CON.jpg") && saved.Contains("_NUL"));
    True(saved.Contains("trailing") && saved.Contains("back_slash.png"));
}

static async Task DirectoryCollisions()
{
    using var directory = new TestDirectory();
    var bytes = Encoding.UTF8.GetBytes("image");
    var paths = new[] { "A?/a.jpg", "A*/b.jpg", "Case/a.jpg", "case/b.jpg", "A?/c.jpg" };
    using var http = DownloadHttp(bytes);
    using var client = new YandexDiskClient(http);
    var result = await new ImageDownloader(client).DownloadAsync(Source(), paths.Select(path => Image(path.Split('/')[^1], path, bytes)).ToArray(), directory.Path, 4, null, default);
    Equal(paths.Length, result.Downloaded);
    Equal(4, Directory.GetDirectories(directory.Path).Length);
    True(File.Exists(System.IO.Path.Combine(directory.Path, "A_", "a.jpg")));
    True(File.Exists(System.IO.Path.Combine(directory.Path, "A_", "c.jpg")));
    True(File.Exists(System.IO.Path.Combine(directory.Path, "A_ (2)", "b.jpg")));
}

static async Task DownloadRetry()
{
    using var directory = new TestDirectory();
    var bytes = Encoding.UTF8.GetBytes("image");
    var calls = 0;
    using var http = new HttpClient(new FakeHandler((request, _) =>
    {
        if (request.RequestUri!.Host != "download.invalid") return Task.FromResult(Json(new { href = "https://download.invalid/original" }));
        calls++;
        if (calls == 1) return Task.FromResult(Transient(HttpStatusCode.ServiceUnavailable));
        if (calls == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }));
    using var client = new YandexDiskClient(http);
    var result = await new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("photo.png", "photo.png", bytes) }, directory.Path, 1, null, default);
    Equal(1, result.Downloaded);
    Equal(3, calls);
    Equal(1, Directory.GetFiles(directory.Path).Length);
}

static async Task Corruption()
{
    foreach (var wrongSize in new[] { false, true })
    {
        using var directory = new TestDirectory();
        var original = Encoding.UTF8.GetBytes("image");
        using var http = DownloadHttp(Encoding.UTF8.GetBytes(wrongSize ? "shorter" : "wrong"));
        using var client = new YandexDiskClient(http);
        var result = await new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("photo.png", "photo.png", original) }, directory.Path, 1, null, default);
        Equal(1, result.Failed);
        True(result.Errors[0].Contains(wrongSize ? "Размер" : "Контрольная"), "Corruption is rejected by size/hash verification.");
        Equal(0, Directory.GetFiles(directory.Path).Length);
    }
}

static async Task DownloadCancellation()
{
    using var directory = new TestDirectory();
    using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host != "download.invalid"
        ? Json(new { href = "https://download.invalid/original" })
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) })));
    using var client = new YandexDiskClient(http);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
    await Throws<OperationCanceledException>(() => new ImageDownloader(client).DownloadAsync(Source(), new[] {
        new RemoteImage("a.png", "/a.png", "a.png", -1, null, null) }, directory.Path, 1, null, cancellation.Token));
    Equal(0, Directory.GetFiles(directory.Path).Length);
}

static async Task Traversal()
{
    using var directory = new TestDirectory();
    var bytes = new byte[] { 1 };
    using var http = DownloadHttp(bytes);
    using var client = new YandexDiskClient(http);
    foreach (var relative in new[] { "../escape.png", "/escape.png", "a/../../escape.png", "./escape.png" })
        await Throws<InvalidDataException>(() => new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("escape.png", relative, bytes) }, directory.Path, 1, null, default));
    Equal(0, Directory.GetFiles(directory.Path).Length);
}

static async Task JunctionProtection()
{
    using var directory = new TestDirectory();
    using var outside = new TestDirectory();
    var link = System.IO.Path.Combine(directory.Path, "link");
    try { Directory.CreateSymbolicLink(link, outside.Path); }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
    {
        if (!OperatingSystem.IsWindows())
            throw new Exception("This Unix filesystem must support symbolic links for the path-escape test.", exception);
        // Windows junctions do not require developer mode or administrator symlink privileges.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(outside.Path);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            // Some workspaces use exFAT, where junction creation is impossible. Check an existing
            // Windows junction instead; the downloader must reject it before creating anything.
            var systemJunction = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "All Users");
            if ((File.GetAttributes(systemJunction) & FileAttributes.ReparsePoint) == 0)
                throw new Exception("The filesystem cannot create junctions and no system junction is available for this test.");
            using var fallbackHttp = DownloadHttp(new byte[] { 1 });
            using var fallbackClient = new YandexDiskClient(fallbackHttp);
            await Throws<IOException>(() => new ImageDownloader(fallbackClient).DownloadAsync(Source(), Array.Empty<RemoteImage>(), systemJunction, 1, null, default));
            Console.WriteLine("INFO Junction creation unavailable on this filesystem; existing Windows junction root was rejected.");
            return;
        }
    }
    try
    {
        var bytes = new byte[] { 1 };
        using var http = DownloadHttp(bytes);
        using var client = new YandexDiskClient(http);
        var result = await new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("a.png", "link/a.png", bytes) }, directory.Path, 1, null, default);
        Equal(1, result.Failed);
        True(result.Errors[0].Contains("символическую ссылку"), "A destination directory link must be rejected before downloading.");
        await Throws<IOException>(() => new ImageDownloader(client).DownloadAsync(Source(), new[] { Image("a.png", "a.png", bytes) }, link, 1, null, default));
        Equal(0, Directory.GetFiles(outside.Path).Length);
    }
    finally { Directory.Delete(link); }
}

static object FileItem(string name, string path, string? media = null, string? mime = null, long size = 1) => new { type = "file", name, path, size, media_type = media, mime_type = mime };
static DiskSource Source() => new("https://disk.yandex.ru/d/A", "/");
static RemoteImage Image(string name, string relative, byte[] bytes) => new(name, "/" + relative, relative, bytes.Length,
    Convert.ToHexString(MD5.HashData(bytes)), Convert.ToHexString(SHA256.HashData(bytes)));
static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
static HttpResponseMessage Transient(HttpStatusCode status) { var response = new HttpResponseMessage(status); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return response; }
static HttpClient DownloadHttp(byte[] bytes, Action? download = null) => new(new FakeHandler((request, _) =>
{
    if (request.RequestUri!.Host == "download.invalid")
    {
        download?.Invoke();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    True(request.RequestUri.AbsolutePath.EndsWith("/download"), "Original-file download endpoint is used.");
    return Task.FromResult(Json(new { href = "https://download.invalid/original" }));
}));
static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : ""));
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
static void True(bool condition, string message = "Assertion failed.") { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected exception " + typeof(T).Name);
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}
sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "YandexImagesTests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
sealed class BrokenStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated transport failure.");
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Simulated transport failure."));
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
sealed class BlockingStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
