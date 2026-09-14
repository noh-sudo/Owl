namespace OwlServer.Models;

/// <summary>
/// cam_log table row. Thumbnail stores a relative file path only (dev plan section 15) -
/// never the image bytes themselves.
/// </summary>
public sealed class CamLog
{
    public int LId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Thumbnail { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
