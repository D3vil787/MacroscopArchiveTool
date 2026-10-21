namespace MacroscopArchiveTool.Core.Migration;

public sealed record VolumeInfo(char Letter, string Label, string FileSystem, long TotalBytes, long FreeBytes, bool IsVirtual, string? BackingFile);

/// <summary>Operating-system actions needed to retire an archive drive. Real implementation: <see cref="WindowsSystemOps"/>.</summary>
public interface ISystemOps
{
    bool IsAdministrator();
    /// <summary>Root folder of the volume mounted at a letter ("J:\\" on Windows).</summary>
    string RootOf(char letter);
    IReadOnlyList<VolumeInfo> GetVolumes();
    char FindFreeLetter(IEnumerable<char> avoid);

    /// <summary>Creates a dynamically expanding VHDX, attaches it, makes one NTFS volume and mounts it at <paramref name="letter"/>.</summary>
    void CreateVhdx(string vhdxPath, long maxBytes, string label, char letter, Action<string> log);
    void AttachVhdx(string vhdxPath, Action<string> log);
    void DetachVhdx(string vhdxPath, Action<string> log);

    /// <summary>Volume GUID path (\\?\Volume{…}\) currently mounted at a drive letter.</summary>
    string GetVolumeId(char letter);
    void RemoveLetter(char letter, Action<string> log);
    void AssignLetter(char letter, string volumeId, Action<string> log);

    /// <summary>Human-readable list of running Macroscop server processes/services (empty = stopped).</summary>
    IReadOnlyList<string> RunningMacroscop();
    /// <summary>Stops Macroscop when it runs as a Windows service. Returns false when it must be stopped by the user (Standalone).</summary>
    bool TryStopMacroscopService(Action<string> log);
    /// <summary>Stops running PostgreSQL services and returns their names (to start them again later).</summary>
    IReadOnlyList<string> StopPostgres(Action<string> log);
    void StartServices(IEnumerable<string> names, Action<string> log);

    /// <summary>Scheduled task at system start-up that attaches the VHDX and mounts it at the letter.</summary>
    void CreateAttachTask(string taskName, string vhdxPath, char letter, Action<string> log);
    void DeleteTask(string taskName, Action<string> log);
    bool TaskExists(string taskName);
}
