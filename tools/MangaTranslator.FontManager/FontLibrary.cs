using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace MangaTranslator.FontManager;

public sealed class FontLibrary
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf",
        ".otf",
        ".ttc",
        ".otc"
    };

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string lockPath;

    public FontLibrary(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Font library root cannot be empty.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        CatalogPath = Path.Combine(RootDirectory, "font_catalog.json");
        FontDirectory = Path.Combine(RootDirectory, "fonts");
        SampleDirectory = Path.Combine(RootDirectory, "samples");
        lockPath = Path.Combine(RootDirectory, ".font-library.lock");
    }

    public string RootDirectory { get; }

    public string CatalogPath { get; }

    public string FontDirectory { get; }

    public string SampleDirectory { get; }

    public void Initialize()
    {
        EnsureDirectories();
        if (File.Exists(CatalogPath))
        {
            return;
        }

        using var mutationLock = AcquireMutationLock();
        EnsureDirectories();
        if (!File.Exists(CatalogPath))
        {
            SaveCatalogAtomic(CreateEmptyCatalog());
        }
    }

    public FontCatalog ReadCatalog()
    {
        Initialize();
        return ReadCatalogWithoutInitialization();
    }

    public FontEntry Add(AddFontRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = PrepareInputs(request);
        var preparedLicense = PrepareLicense(request.LicenseFilePath);

        using var mutationLock = AcquireMutationLock();
        EnsureDirectories();
        var catalog = ReadCatalogWithoutInitialization();
        var fontId = ResolveFontId(catalog, request.FontId);
        if (catalog.Fonts.Any(font => string.Equals(font.FontId, fontId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FontLibraryException($"Font ID already exists: {fontId}");
        }

        var targetDirectory = Path.Combine(FontDirectory, fontId);
        if (Directory.Exists(targetDirectory))
        {
            throw new FontLibraryException(
                $"Managed directory already exists without a catalog entry: fonts/{fontId}. "
                + "Run validate and resolve the orphan before adding this ID.");
        }

        var stagingDirectory = Path.Combine(
            FontDirectory,
            $".staging-{fontId}-{Guid.NewGuid():N}");
        var targetDirectoryCreated = false;
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var copiedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<FontStyleSlot, FontFaceAsset>();
            foreach (var pair in prepared)
            {
                var source = pair.Value;
                var dedupeKey = $"{source.Sha256}:{Path.GetExtension(source.FullPath)}";
                if (!copiedPaths.TryGetValue(dedupeKey, out var destinationFileName))
                {
                    destinationFileName = $"{pair.Key.ToString().ToLowerInvariant()}"
                        + Path.GetExtension(source.FullPath).ToLowerInvariant();
                    var destination = Path.Combine(stagingDirectory, destinationFileName);
                    File.Copy(source.FullPath, destination, overwrite: false);
                    copiedPaths.Add(dedupeKey, destinationFileName);
                }

                assets[pair.Key] = new FontFaceAsset
                {
                    RelativePath = ToRelativePath(Path.Combine("fonts", fontId, destinationFileName)),
                    OriginalFileName = Path.GetFileName(source.FullPath),
                    FaceIndex = source.FaceIndex,
                    Sha256 = source.Sha256,
                    DetectedFamilyName = source.FamilyName,
                    DetectedWeight = source.Weight,
                    DetectedSlant = source.Slant
                };
            }

            string? licenseRelativePath = null;
            if (preparedLicense is not null)
            {
                var licenseFileName = "LICENSE" + Path.GetExtension(preparedLicense.FullPath)
                    .ToLowerInvariant();
                File.Copy(
                    preparedLicense.FullPath,
                    Path.Combine(stagingDirectory, licenseFileName),
                    overwrite: false);
                licenseRelativePath = ToRelativePath(
                    Path.Combine("fonts", fontId, licenseFileName));
            }

            Directory.Move(stagingDirectory, targetDirectory);
            targetDirectoryCreated = true;
            var regular = assets[FontStyleSlot.Regular];
            var entry = new FontEntry
            {
                FontId = fontId,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? regular.DetectedFamilyName ?? Path.GetFileNameWithoutExtension(regular.OriginalFileName)
                    : request.DisplayName.Trim(),
                FamilyName = regular.DetectedFamilyName,
                Category = NormalizeOptional(request.Category),
                Tags = NormalizeTags(request.Tags),
                Source = new FontSourceInfo
                {
                    Kind = string.IsNullOrWhiteSpace(request.SourceKind)
                        ? "file"
                        : request.SourceKind.Trim(),
                    Reference = NormalizeOptional(request.SourceReference),
                    LicenseNote = NormalizeOptional(request.LicenseNote),
                    LicenseRelativePath = licenseRelativePath,
                    LicenseSha256 = preparedLicense?.Sha256
                },
                AddedAtUtc = DateTimeOffset.UtcNow,
                Regular = regular,
                Bold = assets.GetValueOrDefault(FontStyleSlot.Bold),
                Italic = assets.GetValueOrDefault(FontStyleSlot.Italic)
            };
            catalog.Fonts.Add(entry);
            catalog.Fonts = catalog.Fonts
                .OrderBy(font => FontIdNumber(font.FontId))
                .ThenBy(font => font.FontId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            catalog.LibraryRevision++;
            catalog.NextFontNumber = Math.Max(catalog.NextFontNumber, NextNumberAfter(fontId));
            SaveCatalogAtomic(catalog);
            return entry;
        }
        catch
        {
            if (targetDirectoryCreated)
            {
                DeleteDirectoryIfExists(targetDirectory);
            }

            DeleteDirectoryIfExists(stagingDirectory);
            throw;
        }
    }

    public bool Remove(string fontId)
    {
        if (string.IsNullOrWhiteSpace(fontId))
        {
            throw new ArgumentException("Font ID cannot be empty.", nameof(fontId));
        }

        using var mutationLock = AcquireMutationLock();
        EnsureDirectories();
        var catalog = ReadCatalogWithoutInitialization();
        var entry = catalog.Fonts.FirstOrDefault(font => string.Equals(
            font.FontId,
            fontId.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        var managedDirectory = Path.Combine(FontDirectory, entry.FontId);
        EnsureUnderDirectory(managedDirectory, FontDirectory);
        var quarantineDirectory = Path.Combine(
            FontDirectory,
            $".trash-{entry.FontId}-{Guid.NewGuid():N}");
        if (Directory.Exists(managedDirectory))
        {
            Directory.Move(managedDirectory, quarantineDirectory);
        }

        catalog.Fonts.Remove(entry);
        catalog.LibraryRevision++;
        try
        {
            SaveCatalogAtomic(catalog);
        }
        catch
        {
            if (Directory.Exists(quarantineDirectory))
            {
                Directory.Move(quarantineDirectory, managedDirectory);
            }

            throw;
        }

        DeleteDirectoryIfExists(quarantineDirectory);
        return true;
    }

    public FontSampleInfo GenerateSample(SampleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FontSizePx < 8 || options.FontSizePx > 256)
        {
            throw new FontLibraryException("Sample font size must be between 8 and 256 pixels.");
        }

        var lines = options.SampleLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();
        if (lines.Count == 0)
        {
            throw new FontLibraryException("At least one non-empty sample line is required.");
        }

        using var mutationLock = AcquireMutationLock();
        EnsureDirectories();
        var catalog = ReadCatalogWithoutInitialization();
        if (catalog.Fonts.Count == 0)
        {
            throw new FontLibraryException("Cannot generate a sample sheet because the catalog is empty.");
        }

        var fileName = ResolveSampleFileName(options.OutputFileName, catalog.LibraryRevision);
        var outputPath = Path.Combine(SampleDirectory, fileName);
        EnsureUnderDirectory(outputPath, SampleDirectory);
        var temporaryPath = outputPath + $".tmp-{Guid.NewGuid():N}";
        var renderer = new FontSampleRenderer(this);
        FontSampleInfo sample;
        try
        {
            sample = renderer.Render(catalog, lines, options.FontSizePx, temporaryPath);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        sample.RelativePath = ToRelativePath(outputPath);
        catalog.LatestSample = sample;
        SaveCatalogAtomic(catalog);
        return sample;
    }

    public ValidationReport Validate()
    {
        var report = new ValidationReport();
        var catalog = ReadCatalog();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Fonts)
        {
            if (!ids.Add(entry.FontId))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = "error",
                    Message = $"Duplicate font ID: {entry.FontId}"
                });
            }

            if (entry.Regular is null)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = "error",
                    Message = $"{entry.FontId} has no regular face."
                });
            }

            foreach (var asset in EnumerateAssets(entry))
            {
                string absolutePath;
                try
                {
                    absolutePath = ResolveManagedPath(asset.RelativePath);
                }
                catch (FontLibraryException exception)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = "error",
                        Message = $"{entry.FontId}: {exception.Message}"
                    });
                    continue;
                }

                if (!File.Exists(absolutePath))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = "error",
                        Message = $"Missing managed font file: {asset.RelativePath}"
                    });
                    continue;
                }

                var hash = ComputeSha256(absolutePath);
                if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = "error",
                        Message = $"Checksum mismatch: {asset.RelativePath}"
                    });
                }
            }

            ValidateLicense(entry, report);
        }

        if (catalog.LatestSample is not null)
        {
            try
            {
                var samplePath = ResolveManagedPath(catalog.LatestSample.RelativePath);
                if (!File.Exists(samplePath))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = "error",
                        Message = $"Latest sample image is missing: {catalog.LatestSample.RelativePath}"
                    });
                }
            }
            catch (FontLibraryException exception)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = "error",
                    Message = exception.Message
                });
            }

            if (catalog.LatestSample.SourceLibraryRevision != catalog.LibraryRevision)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = "warning",
                    Message = "Latest sample is stale; regenerate it after catalog changes."
                });
            }
        }

        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in catalog.Fonts.SelectMany(EnumerateAssets))
        {
            try
            {
                referencedPaths.Add(Path.GetFullPath(ResolveManagedPath(asset.RelativePath)));
            }
            catch (FontLibraryException exception)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = "error",
                    Message = exception.Message
                });
            }
        }
        foreach (var entry in catalog.Fonts)
        {
            if (string.IsNullOrWhiteSpace(entry.Source.LicenseRelativePath))
            {
                continue;
            }

            try
            {
                referencedPaths.Add(Path.GetFullPath(
                    ResolveManagedPath(entry.Source.LicenseRelativePath)));
            }
            catch (FontLibraryException)
            {
                // The detailed error is added by ValidateLicense.
            }
        }
        if (Directory.Exists(FontDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(FontDirectory, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}.staging-", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}.trash-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!referencedPaths.Contains(Path.GetFullPath(file)))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = "warning",
                        Message = $"Unreferenced file: {ToRelativePath(file)}"
                    });
                }
            }
        }

        return report;
    }

    public string ResolveManagedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new FontLibraryException("Managed path cannot be empty.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(RootDirectory, normalized));
        EnsureUnderDirectory(fullPath, RootDirectory);
        return fullPath;
    }

    private static Dictionary<FontStyleSlot, PreparedFace> PrepareInputs(AddFontRequest request)
    {
        var inputs = new Dictionary<FontStyleSlot, FontStyleInput>
        {
            [FontStyleSlot.Regular] = request.Regular
        };
        if (request.Bold is not null)
        {
            inputs[FontStyleSlot.Bold] = request.Bold;
        }

        if (request.Italic is not null)
        {
            inputs[FontStyleSlot.Italic] = request.Italic;
        }

        return inputs.ToDictionary(pair => pair.Key, pair => Inspect(pair.Value));
    }

    private static PreparedFace Inspect(FontStyleInput input)
    {
        var fullPath = Path.GetFullPath(input.SourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FontLibraryException($"Font file does not exist: {fullPath}");
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new FontLibraryException(
                $"Unsupported font extension '{extension}'. Use TTF, OTF, TTC, or OTC.");
        }

        if (input.FaceIndex < 0)
        {
            throw new FontLibraryException("Font face index cannot be negative.");
        }

        var sha256 = ComputeSha256(fullPath);
        using var typeface = SKTypeface.FromFile(fullPath, input.FaceIndex);
        if (typeface is null)
        {
            throw new FontLibraryException(
                $"SkiaSharp could not read font face {input.FaceIndex} from: {fullPath}");
        }

        return new PreparedFace(
            fullPath,
            input.FaceIndex,
            sha256,
            typeface.FamilyName,
            typeface.FontStyle.Weight,
            typeface.FontStyle.Slant.ToString());
    }

    private static PreparedLicense? PrepareLicense(string? licenseFilePath)
    {
        if (string.IsNullOrWhiteSpace(licenseFilePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(licenseFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FontLibraryException($"License file does not exist: {fullPath}");
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".html", StringComparison.OrdinalIgnoreCase))
        {
            throw new FontLibraryException(
                "License file must use a .txt, .md, or .html extension.");
        }

        return new PreparedLicense(fullPath, ComputeSha256(fullPath));
    }

    private void ValidateLicense(FontEntry entry, ValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(entry.Source.LicenseRelativePath))
        {
            return;
        }

        string absolutePath;
        try
        {
            absolutePath = ResolveManagedPath(entry.Source.LicenseRelativePath);
        }
        catch (FontLibraryException exception)
        {
            report.Issues.Add(new ValidationIssue
            {
                Severity = "error",
                Message = $"{entry.FontId} license: {exception.Message}"
            });
            return;
        }

        if (!File.Exists(absolutePath))
        {
            report.Issues.Add(new ValidationIssue
            {
                Severity = "error",
                Message = $"Missing managed license file: {entry.Source.LicenseRelativePath}"
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.Source.LicenseSha256)
            && !string.Equals(
                ComputeSha256(absolutePath),
                entry.Source.LicenseSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            report.Issues.Add(new ValidationIssue
            {
                Severity = "error",
                Message = $"License checksum mismatch: {entry.Source.LicenseRelativePath}"
            });
        }
    }

    private static string ResolveFontId(FontCatalog catalog, string? requestedId)
    {
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var normalized = requestedId.Trim().ToUpperInvariant();
            if (!IsValidFontId(normalized))
            {
                throw new FontLibraryException(
                    "Font ID must contain 2-64 letters, digits, '-' or '_', start with a "
                    + "letter or digit, and not be a reserved device name.");
            }

            return normalized;
        }

        var next = Math.Max(1, catalog.NextFontNumber);
        while (catalog.Fonts.Any(font => string.Equals(
            font.FontId,
            $"F{next:D4}",
            StringComparison.OrdinalIgnoreCase)))
        {
            next++;
        }

        return $"F{next:D4}";
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(FontDirectory);
        Directory.CreateDirectory(SampleDirectory);
    }

    private FontCatalog ReadCatalogWithoutInitialization()
    {
        if (!File.Exists(CatalogPath))
        {
            SaveCatalogAtomic(CreateEmptyCatalog());
        }

        using var stream = new FileStream(
            CatalogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var catalog = JsonSerializer.Deserialize<FontCatalog>(stream, FontCatalogJson.Options);
        if (catalog is null)
        {
            throw new FontLibraryException($"Catalog is empty: {CatalogPath}");
        }

        if (catalog.SchemaVersion != 1)
        {
            throw new FontLibraryException(
                $"Unsupported font catalog schema version {catalog.SchemaVersion}. Expected 1.");
        }

        catalog.Fonts ??= [];
        return catalog;
    }

    private void SaveCatalogAtomic(FontCatalog catalog)
    {
        EnsureDirectories();
        catalog.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var temporaryPath = CatalogPath + $".tmp-{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(catalog, FontCatalogJson.Options);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Write(Environment.NewLine);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, CatalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private FileStream AcquireMutationLock()
    {
        Directory.CreateDirectory(RootDirectory);
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new FontLibraryException(
                $"The font library is busy. Close the other font-manager operation and retry. ({exception.Message})",
                exception);
        }
    }

    private static FontCatalog CreateEmptyCatalog()
    {
        return new FontCatalog
        {
            SchemaVersion = 1,
            LibraryRevision = 0,
            NextFontNumber = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        return tags
            .SelectMany(tag => tag.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsValidFontId(string id)
    {
        if (id.Length is < 2 or > 64 || !char.IsLetterOrDigit(id[0]))
        {
            return false;
        }

        return !ReservedFileNames.Contains(id)
            && id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    private static int FontIdNumber(string id)
    {
        if (id.Length > 1 && id[0] == 'F' && int.TryParse(id[1..], out var number))
        {
            return number;
        }

        return int.MaxValue;
    }

    private static int NextNumberAfter(string id)
    {
        var number = FontIdNumber(id);
        return number < int.MaxValue ? number + 1 : 1;
    }

    private static string ResolveSampleFileName(string? requestedFileName, int revision)
    {
        var fileName = $"font_samples_r{revision:D4}.png";
        if (!string.IsNullOrWhiteSpace(requestedFileName))
        {
            var requested = requestedFileName.Trim();
            if (requested is "." or "..")
            {
                throw new FontLibraryException("Sample output must be a PNG file name.");
            }

            fileName = Path.GetFileName(requested);
            if (!string.Equals(fileName, requested, StringComparison.Ordinal))
            {
                throw new FontLibraryException(
                    "Sample output must be a file name inside the managed samples directory.");
            }
        }

        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".png";
        }

        return fileName;
    }

    private string ToRelativePath(string path)
    {
        var absolutePath = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(RootDirectory, path));
        return Path.GetRelativePath(RootDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static IEnumerable<FontFaceAsset> EnumerateAssets(FontEntry entry)
    {
        if (entry.Regular is not null)
        {
            yield return entry.Regular;
        }

        if (entry.Bold is not null)
        {
            yield return entry.Bold;
        }

        if (entry.Italic is not null)
        {
            yield return entry.Italic;
        }
    }

    private static void EnsureUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullDirectory, comparison))
        {
            throw new FontLibraryException($"Path escapes the managed directory: {path}");
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record PreparedFace(
        string FullPath,
        int FaceIndex,
        string Sha256,
        string? FamilyName,
        int Weight,
        string Slant);

    private sealed record PreparedLicense(string FullPath, string Sha256);
}

public sealed class FontLibraryException : Exception
{
    public FontLibraryException(string message)
        : base(message)
    {
    }

    public FontLibraryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
