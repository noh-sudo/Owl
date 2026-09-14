namespace OwlServer.Utils;

/// <summary>
/// Thin clock abstraction so services don't call DateTime.Now/UtcNow directly
/// (keeps timestamp generation swappable for tests). Named IClock/SystemClock
/// to avoid colliding with the BCL's System.TimeProvider.
/// </summary>
public interface IClock
{
    DateTime Now { get; }
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
    public DateTime UtcNow => DateTime.UtcNow;
}
