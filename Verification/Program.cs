using System.Security.Cryptography;
using System.Text.Json;
using YandexImages.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: YandexImages.Verification <public-folder-url> <output-directory> [--download-one]");
    return 2;
}
var source = DiskSource.Parse(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var client = new YandexDiskClient();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var images = await client.ListImagesAsync(source, recursive: true, timeout.Token);
Console.WriteLine($"Listed {images.Count} images, {images.Sum(image => image.Size)} bytes.");
if (!args.Contains("--download-one", StringComparer.Ordinal)) return 0;
if (images.Count == 0) throw new Exception("No images returned for this public folder.");
var sample = images.MinBy(image => image.Size)!;
IReadOnlyList<RemoteImage> one = new[] { sample };
var first = await new ImageDownloader(client).DownloadAsync(source, one, output, 2, null, timeout.Token);
if (first.Failed != 0 || first.Downloaded + first.Skipped != 1)
    throw new Exception("Live download failed: " + string.Join("; ", first.Errors));
var localFile = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
    .FirstOrDefault(file => !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length == sample.Size)
    ?? throw new Exception("Downloaded image is missing from the output tree.");
var relativeFile = Path.GetRelativePath(output, localFile);
if (sample.RelativePath.Contains('/') && Path.GetDirectoryName(relativeFile) is "" or null)
    throw new Exception("Nested folder structure was not preserved.");
await using (var stream = File.OpenRead(localFile))
{
    var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream));
    if (!string.IsNullOrWhiteSpace(sample.Sha256) && !checksum.Equals(sample.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Independent SHA256 validation failed.");
}
var second = await new ImageDownloader(client).DownloadAsync(source, one, output, 2, null, timeout.Token);
if (second.Skipped != 1 || second.Failed != 0 || second.Downloaded != 0)
    throw new Exception("A verified existing image was not skipped.");
var evidence = new
{
    checkedAt = DateTimeOffset.UtcNow,
    source.PublicKey,
    source.Path,
    imageCount = images.Count,
    totalBytes = images.Sum(image => image.Size),
    sample = new { sample.Name, sample.Size, sample.Sha256 },
    relativeFile,
    first,
    second,
    independentSha256 = "passed"
};
var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), json);
Console.WriteLine(json);
return 0;
