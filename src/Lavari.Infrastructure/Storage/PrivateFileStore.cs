using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;
using Microsoft.Extensions.Options;

namespace Lavari.Infrastructure.Storage;

public sealed class LavariStorageOptions
{
    public string StorageRoot { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
}

/// <summary>
/// Platform-aware private local disk storage service using opaque keys, staged atomic writes,
/// and path-traversal protection. Never stores persistent files inside build output or web root.
/// </summary>
public sealed class LocalPrivateFileStore : IPrivateFileStore
{
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _timeProvider;

    public string StorageRoot { get; }

    public LocalPrivateFileStore(
        IOptions<LavariStorageOptions> options,
        IIdGenerator ids,
        TimeProvider timeProvider)
    {
        _ids = ids;
        _timeProvider = timeProvider;

        var configured = options.Value.StorageRoot;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            StorageRoot = Path.GetFullPath(configured.Trim());
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var baseDir = string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lavari")
                : Path.Combine(localAppData, "Lavari");
            StorageRoot = Path.GetFullPath(Path.Combine(baseDir, "Storage"));
        }

        Directory.CreateDirectory(StorageRoot);
    }

    public async Task<string> SaveAsync(string category, string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var safeCategory = SanitizeSegment(category);
        var safeExt = NormalizeExtension(extension);
        var now = _timeProvider.GetUtcNow();
        var opaqueName = $"{_ids.NewId():N}{safeExt}";
        var key = $"{safeCategory}/{now:yyyy}/{now:MM}/{opaqueName}";

        var fullPath = ResolveSafePath(key);
        var parentDir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parentDir);

        var tempPath = Path.Combine(parentDir, $".tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await fs.WriteAsync(content, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: false);
            return key;
        }
        catch (Exception ex) when (ex is not ServiceException)
        {
            TryDeleteFile(tempPath);
            throw new ExternalIntegrationServiceException(
                "The file could not be saved to private storage.",
                ErrorCodes.StorageFailure,
                "Verify storage availability and permissions, then retry.",
                ex.Message,
                ex);
        }
    }

    public async Task<byte[]> ReadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolveSafePath(storageKey);
        if (!File.Exists(fullPath))
        {
            throw new NotFoundServiceException(
                "The requested file is not available in private storage.",
                "It may have been moved or not yet generated.");
        }

        try
        {
            return await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ServiceException)
        {
            throw new ExternalIntegrationServiceException(
                "The file could not be read from private storage.",
                ErrorCodes.StorageFailure,
                "Try again or contact an administrator.",
                ex.Message,
                ex);
        }
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolveSafePath(storageKey);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task DeleteTemporaryAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolveSafePath(storageKey);
        TryDeleteFile(fullPath);
        return Task.CompletedTask;
    }

    private string ResolveSafePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            storageKey.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(storageKey))
        {
            throw new ValidationServiceException("Invalid storage key.", ErrorCodes.ValidationFailed);
        }

        var parts = storageKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var combined = Path.GetFullPath(Path.Combine([StorageRoot, .. parts]));
        var rootWithSep = StorageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? StorageRoot
            : StorageRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationServiceException("Invalid storage key path.", ErrorCodes.ValidationFailed);
        }

        return combined;
    }

    private static string SanitizeSegment(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var lower = category.Trim().ToLowerInvariant();
        foreach (var ch in lower)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '-' and not '_')
            {
                throw new ValidationServiceException($"Invalid storage category '{category}'.", ErrorCodes.ValidationFailed);
            }
        }
        return lower;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        var clean = extension.Trim().ToLowerInvariant();
        if (!clean.StartsWith('.'))
        {
            clean = "." + clean;
        }

        foreach (var ch in clean.AsSpan(1))
        {
            if (!char.IsLetterOrDigit(ch))
            {
                throw new ValidationServiceException($"Invalid file extension '{extension}'.", ErrorCodes.ValidationFailed);
            }
        }
        return clean;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temporary cleanup; never masks the primary error.
        }
    }
}
