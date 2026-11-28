using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Homebase.Core;

/// <summary>Turns a picture a browser can't draw into a JPEG one can.</summary>
public interface IImageConverter
{
    /// <summary>Whether this host has a way to do it at all.</summary>
    bool Available { get; }

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> as a JPEG no wider or
    /// taller than <paramref name="longestSide"/>, and no bigger than it was.
    /// </summary>
    Task ConvertAsync(string source, string destination, int longestSide, CancellationToken cancellationToken);
}

/// <summary>
/// macOS's own image tool, which reads whatever the Mac can open, HEIC included. Linux has nothing
/// like it built in, so a Linux host shows HEIC only in a browser that draws it itself (Safari).
/// </summary>
public sealed class SipsImageConverter : IImageConverter
{
    private const string Sips = "/usr/bin/sips";

    public bool Available => OperatingSystem.IsMacOS() && File.Exists(Sips);

    public async Task ConvertAsync(string source, string destination, int longestSide, CancellationToken cancellationToken)
    {
        // Asked for a size, sips makes a picture that size even when that means enlarging it.
        var sizes = await RunAsync(["-g", "pixelWidth", "-g", "pixelHeight", source], cancellationToken);
        var longest = sizes.Split('\n')
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim() is "pixelWidth" or "pixelHeight")
            .Select(parts => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixels) ? pixels : 0)
            .DefaultIfEmpty(0)
            .Max();
        List<string> arguments = ["-s", "format", "jpeg", "-s", "formatOptions", "82"];
        if (longest > longestSide) arguments.AddRange(["-Z", longestSide.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange([source, "--out", destination]);
        await RunAsync(arguments, cancellationToken);
        if (!File.Exists(destination)) throw new IOException("sips didn’t write the picture.");
    }

    private static async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Sips)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("sips didn’t start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new IOException($"sips stopped with {process.ExitCode}: {(await error).Trim()}");
            return await output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) ProcessTree.Kill(process);
            throw;
        }
    }
}

/// <summary>
/// JPEGs of photos browsers can't draw — HEIC, which is what iPhones take — made on the host so
/// they can be looked at in any browser. Each is made once, the first time it's looked at, and kept
/// under the account's <c>.homebase</c>, where nothing is browsed, copied or synced. They're only a
/// convenience, made again from the photo whenever they're needed: the oldest looked at are thrown
/// away past a limit, backups leave them out, and they aren't counted in the space an account uses
/// (though the host's own limit, on all of Uncloud's room on the drive, does count them).
/// </summary>
public sealed class PhotoPreviews(IImageConverter converter, ILogger<PhotoPreviews> logger)
{
    /// <summary>Plenty for any screen the viewer fills, and a fifth of a full-size photo.</summary>
    public const int LongestSide = 2560;

    /// <summary>Where they're kept, inside the account's <c>.homebase</c>.</summary>
    public const string FolderName = "previews";

    /// <summary>How much they may take up for one account: about a thousand photos.</summary>
    public long Budget { get; init; } = 512L * 1024 * 1024;

    /// <summary>How often, at most, the folder is looked over for ones to throw away.</summary>
    public TimeSpan TidyEvery { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long one photo may take. sips takes a third of a second for a 36-megapixel one.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(1);

    // A few at a time across the host: a whole folder of photos flicked through at once shouldn't
    // start one converter per photo.
    private readonly SemaphoreSlim _converting = new(2, 2);
    // The same photo asked for twice at once — by its details and the viewer — is made once.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _making = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _trimmed = new(StringComparer.Ordinal);

    /// <summary>Whether a file of this name is only shown in a browser once it's been made into a JPEG.</summary>
    public static bool NeedsConverting(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".heic" or ".heif";

    /// <summary>
    /// The JPEG to show for <paramref name="photo"/>, one of the account's files, opened already: made
    /// now if it hasn't been, and null when this host can't make one, or couldn't this time.
    /// </summary>
    public async Task<string?> JpegForAsync(string root, FileStream photo, CancellationToken cancellationToken)
    {
        if (!converter.Available) return null;
        var source = new FileInfo(photo.Name);
        var folder = Folder(root);
        // A photo changed in place is a different photo: its JPEG is made again rather than shown stale.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetRelativePath(root, source.FullName)}\n{photo.Length}\n{source.LastWriteTimeUtc.Ticks}")))[..32].ToLowerInvariant();
        var jpeg = Path.Combine(folder, key + ".jpg");
        if (File.Exists(jpeg))
        {
            Touch(jpeg);
            return jpeg;
        }
        var making = _making.GetOrAdd(jpeg, _ => new Lazy<Task>(() => MakeAsync(source.FullName, jpeg, folder)));
        try
        {
            // Somebody who stops waiting doesn't stop it being made: the next look finds it ready.
            await making.Value.WaitAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Couldn’t make a JPEG of a photo to show it");
            return null;
        }
        return File.Exists(jpeg) ? jpeg : null;
    }

    private async Task MakeAsync(string source, string jpeg, string folder)
    {
        await Task.Yield();
        var part = Path.Combine(folder, $"{Guid.NewGuid():N}.part.jpg");
        await _converting.WaitAsync();
        try
        {
            using var limit = new CancellationTokenSource(Limit);
            await converter.ConvertAsync(source, part, LongestSide, limit.Token);
            File.Move(part, jpeg, overwrite: true);
        }
        finally
        {
            _converting.Release();
            _making.TryRemove(jpeg, out _);
            try { File.Delete(part); } catch (IOException) { }
            Trim(folder);
        }
    }

    private static string Folder(string root)
    {
        var folder = Path.Combine(Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!, FolderName);
        PathPolicy.RejectLink(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Marks one as just looked at, so it's among the last to go.</summary>
    private static void Touch(string jpeg)
    {
        try { File.SetLastWriteTimeUtc(jpeg, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Past the budget, throws away the ones looked at longest ago, down to three quarters of it.</summary>
    private void Trim(string folder)
    {
        var now = DateTime.UtcNow;
        if (_trimmed.TryGetValue(folder, out var last) && now - last < TidyEvery) return;
        _trimmed[folder] = now;
        try
        {
            var all = new DirectoryInfo(folder).GetFiles("*.jpg");
            // A part is one being made. One an hour old was left by a host that stopped partway.
            foreach (var part in all.Where(file => file.Name.EndsWith(".part.jpg", StringComparison.Ordinal)))
                if (now - part.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    try { part.Delete(); } catch (IOException) { }
            var files = all.Where(file => !file.Name.EndsWith(".part.jpg", StringComparison.Ordinal))
                .OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            if (total <= Budget) return;
            foreach (var file in files)
            {
                if (total <= Budget * 3 / 4) break;
                try
                {
                    file.Delete();
                    total -= file.Length;
                }
                catch (IOException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Couldn’t tidy away old photo previews");
        }
    }
}
