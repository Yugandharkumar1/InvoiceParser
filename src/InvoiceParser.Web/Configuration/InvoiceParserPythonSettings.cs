namespace InvoiceParser.Web.Configuration;

/// <summary>Python FastAPI hybrid invoice parser (LayoutLM + rules). Disabled by default.</summary>
public sealed class InvoiceParserPythonSettings
{
    public const string SectionName = "InvoiceParserPython";

    /// <summary>When false, the C# pipeline runs unchanged with no HTTP calls to Python.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL, e.g. http://localhost:8765/ (trailing slash optional).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8765/";

    /// <summary>POST /feedback to Python after save when <see cref="SubmitFeedbackOnSave"/> is true.</summary>
    public bool SubmitFeedbackOnSave { get; set; }

    /// <summary>
    /// When true, POST admin/retrain after each successful feedback (in addition to Python's automatic retrain by sample count).
    /// Normally false — the service already retrains when <c>min_new_feedback_samples</c> is reached.
    /// </summary>
    public bool TriggerRetrainAfterFeedback { get; set; }
}
