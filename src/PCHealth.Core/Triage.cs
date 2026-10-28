namespace PCHealth.Core;

/// <summary>
/// A suggested category for loose files in Desktop, Downloads and Documents (spec §5.2). A suggestion, never a
/// certainty; users can exclude whole categories just as they exclude finding categories.
/// </summary>
public enum TriageCategory
{
    Installers,

    /// <summary>Archives with a folder of the same name next to them.</summary>
    ExtractedArchives,
    DiskImages,
    Screenshots,

    /// <summary>Possible financial or tax documents. Sensitive.</summary>
    FinancialDocuments,

    /// <summary>Possible signed documents. Sensitive.</summary>
    SignedDocuments,

    /// <summary>Files with a newer version next to them.</summary>
    SupersededFiles,
}
