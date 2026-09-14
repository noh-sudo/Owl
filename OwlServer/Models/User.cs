namespace OwlServer.Models;

/// <summary>u_info table row. `Pw` always holds a bcrypt hash, never plaintext.</summary>
public sealed class User
{
    public int UId { get; set; }
    public string UName { get; set; } = string.Empty;
    public string Pw { get; set; } = string.Empty;
}
