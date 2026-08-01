namespace MauiRemoteAdb.Models;

public class ConnectionHistoryItem
{
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public DateTime LastUsed { get; set; }

    public string DisplayText => $"{Host}:{Port}";
    public string DisplaySubText => $"Dùng lần cuối: {LastUsed:dd/MM/yyyy HH:mm}";
}
