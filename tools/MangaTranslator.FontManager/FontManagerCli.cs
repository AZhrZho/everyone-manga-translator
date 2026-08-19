using System.Globalization;
using System.Text.Json;

namespace MangaTranslator.FontManager;

public static class FontManagerCli
{
    private const string DefaultStore = "resources/font-library";

    public static int Run(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Command is null
                || options.Has("help")
                || options.Command is "help" or "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            EnsureKnownOptions(options);
            var library = new FontLibrary(ResolveStore(options.Get("store")));
            return options.Command.ToLowerInvariant() switch
            {
                "init" => Initialize(library),
                "add" => Add(library, options),
                "remove" => Remove(library, options),
                "list" => List(library, options),
                "sample" or "samples" => GenerateSample(library, options),
                "validate" or "verify" => Validate(library, options),
                _ => UnknownCommand(options.Command)
            };
        }
        catch (FontLibraryException exception)
        {
            Console.Error.WriteLine($"font-manager: {exception.Message}");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"font-manager: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"font-manager: unexpected {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int Initialize(FontLibrary library)
    {
        library.Initialize();
        Console.WriteLine($"Initialized font library: {library.RootDirectory}");
        Console.WriteLine($"  catalog: {library.CatalogPath}");
        Console.WriteLine($"  fonts:   {library.FontDirectory}");
        Console.WriteLine($"  samples: {library.SampleDirectory}");
        return 0;
    }

    private static int Add(FontLibrary library, CliOptions options)
    {
        var regular = new FontStyleInput
        {
            SourcePath = options.Require("regular"),
            FaceIndex = options.GetInt("regular-index", 0)
        };
        var request = new AddFontRequest
        {
            FontId = options.Get("id"),
            DisplayName = options.Get("name"),
            Category = options.Get("category"),
            Tags = options.GetList("tags"),
            SourceKind = options.Get("source-kind") ?? "file",
            SourceReference = options.Get("source"),
            LicenseNote = options.Get("license-note"),
            LicenseFilePath = options.Get("license-file"),
            Regular = regular,
            Bold = options.Has("bold")
                ? new FontStyleInput
                {
                    SourcePath = options.Require("bold"),
                    FaceIndex = options.GetInt("bold-index", 0)
                }
                : null,
            Italic = options.Has("italic")
                ? new FontStyleInput
                {
                    SourcePath = options.Require("italic"),
                    FaceIndex = options.GetInt("italic-index", 0)
                }
                : null
        };
        var entry = library.Add(request);
        Console.WriteLine($"Added {entry.FontId}: {entry.DisplayName}");
        Console.WriteLine($"  family: {entry.FamilyName ?? "(unknown)"}");
        Console.WriteLine($"  regular: {entry.Regular?.RelativePath}");
        Console.WriteLine(
            $"  bold:    {(entry.Bold is null ? "(sample will synthesize)" : entry.Bold.RelativePath)}");
        Console.WriteLine(
            $"  italic:  {(entry.Italic is null ? "(sample will synthesize)" : entry.Italic.RelativePath)}");
        return 0;
    }

    private static int Remove(FontLibrary library, CliOptions options)
    {
        var id = options.Get("id") ?? options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException(
                "remove requires --id F0001 (or a positional font ID).");
        }

        if (!library.Remove(id))
        {
            Console.Error.WriteLine($"Font ID not found: {id}");
            return 1;
        }

        Console.WriteLine(
            $"Removed {id.ToUpperInvariant()} from the managed library. "
            + "System fonts were not changed.");
        return 0;
    }

    private static int List(FontLibrary library, CliOptions options)
    {
        var catalog = library.ReadCatalog();
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(catalog, FontCatalogJson.Options));
            return 0;
        }

        Console.WriteLine($"Font library: {library.RootDirectory}");
        Console.WriteLine($"Revision {catalog.LibraryRevision}; {catalog.Fonts.Count} font families");
        if (catalog.Fonts.Count == 0)
        {
            Console.WriteLine("(empty)");
        }
        else
        {
            foreach (var entry in catalog.Fonts)
            {
                var variants = string.Join(", ", new[]
                {
                    entry.Regular is null ? null : "regular",
                    entry.Bold is null ? null : "bold",
                    entry.Italic is null ? null : "italic"
                }.Where(value => value is not null));
                Console.WriteLine(
                    $"{entry.FontId,-8} {entry.DisplayName,-24} "
                    + $"{entry.FamilyName ?? "(unknown)",-24} [{variants}]"
                    + (string.IsNullOrWhiteSpace(entry.Category)
                        ? string.Empty
                        : $"  {entry.Category}"));
            }
        }

        if (catalog.LatestSample is not null)
        {
            var stale = catalog.LatestSample.SourceLibraryRevision != catalog.LibraryRevision
                ? " (stale)"
                : string.Empty;
            Console.WriteLine($"Latest sample: {catalog.LatestSample.RelativePath}{stale}");
        }

        return 0;
    }

    private static int GenerateSample(FontLibrary library, CliOptions options)
    {
        var lines = options.Get("text") is { } text
            ? text.Split(
                '|',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        var sampleLines = options.Has("text")
            ? lines ?? []
            : new SampleOptions().SampleLines;
        var sample = library.GenerateSample(new SampleOptions
        {
            FontSizePx = options.GetInt("font-size", 52),
            SampleLines = sampleLines,
            OutputFileName = options.Get("output")
        });
        Console.WriteLine(
            $"Generated sample sheet: {library.ResolveManagedPath(sample.RelativePath)}");
        Console.WriteLine(
            $"  size: {sample.WidthPx} x {sample.HeightPx}px; "
            + $"sample glyph size: {sample.FontSizePx}px");
        Console.WriteLine($"  source library revision: {sample.SourceLibraryRevision}");
        return 0;
    }

    private static int Validate(FontLibrary library, CliOptions options)
    {
        var report = library.Validate();
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, FontCatalogJson.Options));
        }
        else if (report.Issues.Count == 0)
        {
            Console.WriteLine("Font library is valid.");
        }
        else
        {
            foreach (var issue in report.Issues)
            {
                Console.WriteLine($"{issue.Severity}: {issue.Message}");
            }
        }

        return report.IsValid ? 0 : 1;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void EnsureKnownOptions(CliOptions options)
    {
        var allowed = options.Command?.ToLowerInvariant() switch
        {
            "init" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store"
            },
            "add" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store", "regular", "bold", "italic", "name", "id", "category", "tags",
                "source-kind", "source", "license-note", "license-file", "regular-index",
                "bold-index", "italic-index"
            },
            "remove" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store", "id"
            },
            "list" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store", "json"
            },
            "sample" or "samples" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store", "font-size", "text", "output"
            },
            "validate" or "verify" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "store", "json"
            },
            _ => null
        };
        if (allowed is null)
        {
            return;
        }

        var unknown = options.Keys
            .Where(key => !allowed.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown option(s) for '{options.Command}': {string.Join(", ", unknown.Select(key => $"--{key}"))}");
        }
    }

    private static string ResolveStore(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        return repositoryRoot is null
            ? DefaultStore
            : Path.Combine(repositoryRoot, "resources", "font-library");
    }

    private static string? FindRepositoryRoot(string startingDirectory)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(startingDirectory));
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "MangaTranslator Font Manager\n"
            + "\n"
            + "A filesystem-backed font catalog. It copies input files into its own store;\n"
            + "it never installs, removes, or changes operating-system fonts.\n"
            + "\n"
            + "Commands:\n"
            + "  init     [--store DIR]\n"
            + "  add      [--store DIR] --regular FILE [--bold FILE] [--italic FILE]\n"
            + "           [--name NAME] [--id F0001] [--category TEXT] [--tags a,b]\n"
            + "           [--regular-index N] [--bold-index N] [--italic-index N]\n"
            + "           [--source-kind file|system|download] [--source TEXT]\n"
            + "           [--license-note TEXT] [--license-file FILE]\n"
            + "  remove   [--store DIR] --id F0001\n"
            + "  list     [--store DIR] [--json]\n"
            + "  sample   [--store DIR] [--font-size 52]\n"
            + "           [--text 'line 1|line 2'] [--output FILE.png]\n"
            + "  validate [--store DIR] [--json]\n"
            + "\n"
            + "Default store: resources/font-library under the nearest solution root\n"
            + "Managed layout: font_catalog.json, fonts/F0001/*, samples/*.png\n");
    }

    private sealed class CliOptions
    {
        private readonly Dictionary<string, string?> values =
            new(StringComparer.OrdinalIgnoreCase);

        private CliOptions(string? command)
        {
            Command = command;
        }

        public string? Command { get; }

        public List<string> Positional { get; } = [];

        public IEnumerable<string> Keys => values.Keys;

        public static CliOptions Parse(string[] args)
        {
            if (args.Length == 0)
            {
                return new CliOptions(null);
            }

            var options = new CliOptions(args[0]);
            for (var index = 1; index < args.Length; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Positional.Add(token);
                    continue;
                }

                var key = token[2..];
                if (key.Length == 0)
                {
                    throw new ArgumentException("Option name cannot be empty.");
                }

                if (index + 1 < args.Length
                    && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options.values[key] = args[++index];
                }
                else
                {
                    options.values[key] = null;
                }
            }

            return options;
        }

        public bool Has(string key)
        {
            return values.ContainsKey(key);
        }

        public string? Get(string key)
        {
            return values.GetValueOrDefault(key);
        }

        public string Require(string key)
        {
            var value = Get(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Missing required option --{key}.");
            }

            return value;
        }

        public int GetInt(string key, int defaultValue)
        {
            var value = Get(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
            {
                throw new ArgumentException($"Option --{key} must be an integer.");
            }

            return parsed;
        }

        public string[] GetList(string key)
        {
            return (Get(key) ?? string.Empty)
                .Split(
                    [',', ';'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
