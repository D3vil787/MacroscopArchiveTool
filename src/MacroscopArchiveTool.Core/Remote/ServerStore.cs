using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroscopArchiveTool.Core.Remote;

/// <summary>Another Macroscop server whose archive is scanned from here.</summary>
public sealed class RemoteServer
{
    /// <summary>Name or IP address.</summary>
    public string Host { get; set; } = "";
    /// <summary>Optional label shown in the window (otherwise the host).</summary>
    public string? Title { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Account on that server ("Administrator" or "SERVER\Administrator"); empty = the current Windows account.</summary>
    public string? User { get; set; }
    /// <summary>Password encrypted with Windows DPAPI for the current user of this computer; never stored or shown in plain text.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Title) ? Host : $"{Title} ({Host})";
    [JsonIgnore] public bool HasCredentials => !string.IsNullOrWhiteSpace(User);

    /// <summary>"Administrator" on a workgroup server means "HOST\Administrator".</summary>
    [JsonIgnore] public string? QualifiedUser => !HasCredentials ? null : User!.Contains('\\') || User.Contains('@') ? User : $"{Host}\\{User}";
}

/// <summary>List of remote servers, kept in %ProgramData%\MacroscopArchiveTool\servers.json (passwords DPAPI-encrypted).</summary>
public sealed class ServerStore
{
    public List<RemoteServer> Servers { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(ToolConfig.JsonOptions) { WriteIndented = true };

    public static ServerStore Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ServerStore>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
