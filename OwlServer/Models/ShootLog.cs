namespace OwlServer.Models;

/// <summary>
/// shoot_log table row - records the WPF operator's approve/stop decision for a
/// detection event. This is a UI approval/audit record only; the server does not
/// issue any weapon-firing control command (see dev plan section 1, "안전 범위").
/// </summary>
public sealed class ShootLog
{
    public int SId { get; set; }
    public int UId { get; set; }
    public int? LId { get; set; }
    public bool IsShoot { get; set; }
    public DateTime CreatedAt { get; set; }
}
