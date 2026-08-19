using System.Text.Json;

namespace MangaTranslator.Precheck;

public static class MinimalPrecheckPromptCatalog
{
    public static string ScanSystem(string targetLanguage) => $"""
        You perform the minimal whole-book preprocessing pass for manga translation.
        Your only responsibilities are:
        1. classify every OWNER page by its visible function in the book;
        2. extract compact book-level translation context from the supplied pages.

        Allowed page_type values:
        story, bonus_story, cover, title_page, table_of_contents, credits,
        advertisement, blank, illustration, unknown.

        Context pages are evidence only. Return page classifications only for OWNER pages.
        Do not analyze OCR quality, masks, lettering, layout complexity, cleaning risk,
        cross-page image geometry, or downstream processing hints.
        Preserve uncertainty instead of inventing facts. Generated explanatory text must use {targetLanguage}.
        Return only JSON matching the schema.
        """;

    public static string ScanUser(
        MinimalPrecheckScanBatch batch,
        IReadOnlyDictionary<string, PageDescriptor> pages)
    {
        var payload = new
        {
            batch.BatchId,
            owner_pages = batch.OwnerPages.Select(pageId => Page(pages[pageId], "owner")),
            context_before = batch.ContextPagesBefore.Select(pageId => Page(pages[pageId], "context")),
            context_after = batch.ContextPagesAfter.Select(pageId => Page(pages[pageId], "context")),
            context_delta_contract = new
            {
                batch_synopsis = "compact summary of visible events",
                characters = "names, proposed target names, role, speech traits, relationships, first seen order",
                glossary = "recurring names, places, organizations, objects, techniques, phrases and proposed translations",
                continuity_facts = "facts or reveal-sensitive information with first revealed page order",
                translation_style_signals = "tone, register, honorific, naming and dialogue consistency signals"
            }
        };
        return "Analyze the supplied ordered pages. Each following image is preceded by its exact page identity.\n"
               + JsonSerializer.Serialize(payload);
    }

    public static string ConsolidationSystem(string targetLanguage) => $"""
        You consolidate minimal manga preprocessing scan results into one translation book context.
        Produce a compact, practical reference for consistent page translation in {targetLanguage}.
        Deduplicate characters and terms by real identity, preserve original names, choose stable target-language
        renderings, record speech rules and relationships, and keep reveal timing conservative.
        Do not produce OCR, masking, cleaning, layout, typography, or lettering guidance.
        If evidence conflicts, choose the safest non-spoiling interpretation and record the uncertainty.
        Return only JSON matching the schema.
        """;

    public static string ConsolidationUser(
        MinimalPrecheckRequest request,
        MinimalPageManifestDocument manifest,
        IReadOnlyList<MinimalPrecheckScanResult> scans)
    {
        var payload = new
        {
            book_id = request.BookId,
            target_language = request.Options.TargetLanguage,
            page_manifest = manifest.Pages.Select(page => new
            {
                page.PageId,
                page.Order,
                page.PageType,
                page.IncludeInWorkflow
            }),
            scan_results = scans.Select(scan => new
            {
                scan.BatchId,
                scan.OwnerPages,
                context_delta = scan.ContextDelta,
                unresolved_questions = scan.UnresolvedQuestions
            })
        };
        return "Create the final translation context from this complete-book evidence package:\n"
               + JsonSerializer.Serialize(payload);
    }

    private static object Page(PageDescriptor page, string role) => new
    {
        page.PageId,
        order = page.SourceIndex,
        page.FileName,
        page.Width,
        page.Height,
        role
    };
}
