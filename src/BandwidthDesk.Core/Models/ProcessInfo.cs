namespace BandwidthDesk.Core.Models;

public sealed record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    string? Description,
    string? CompanyName,
    long WorkingSetBytes,
    bool IsMicrosoft);
