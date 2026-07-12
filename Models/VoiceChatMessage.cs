namespace ZapretUI.Models;

public enum VoiceMessageRole
{
    User,
    Assistant,
    System
}

public sealed class VoiceChatMessage
{
    public VoiceMessageRole Role { get; init; }
    public string Text { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string? AgentName { get; init; }
    public bool IsStreaming { get; set; }
}
