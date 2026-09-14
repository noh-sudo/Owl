namespace OwlServer.Models;

/// <summary>
/// The single most-recent JPEG frame held in memory (dev plan section 20 - the
/// server never buffers a full video history, only the latest frame).
/// </summary>
public sealed record VideoFrame(byte[] Jpeg, DateTime ReceivedAt);
