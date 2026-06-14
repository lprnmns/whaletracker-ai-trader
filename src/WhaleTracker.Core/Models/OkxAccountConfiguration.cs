namespace WhaleTracker.Core.Models;

public class OkxAccountConfiguration
{
    public string AccountLevel { get; set; } = string.Empty;
    public string PositionMode { get; set; } = string.Empty;
    public string MarginMode { get; set; } = string.Empty;
    public bool IsDemo { get; set; }
}
