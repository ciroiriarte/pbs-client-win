using System.Runtime.InteropServices;
using System.Text.Json;

namespace Pbs.Protocol;

/// <summary>Persists short-lived PBS login tickets under the user's profile config directory.</summary>
public sealed class TicketStore
{
    private readonly string _directory;

    public TicketStore(string? profileDirectory = null)
    {
        _directory = profileDirectory ?? DefaultTicketDirectory();
    }

    public void Save(string host, PbsTicket ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(ticket);

        EnsureDirectory();
        var path = PathFor(host, ticket.Username);
        File.WriteAllText(path, JsonSerializer.Serialize(ticket, new JsonSerializerOptions { WriteIndented = true }));
        RestrictFile(path);
    }

    public PbsTicket? TryLoad(string host, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var path = PathFor(host, username);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PbsTicket>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Delete(string host, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var path = PathFor(host, username);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string host, string username) =>
        Path.Combine(_directory, Sanitize($"{host}_{username}") + ".json");

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        RestrictDirectory(_directory);
    }

    private static string DefaultTicketDirectory()
    {
        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(root, "proxmox-backup-client", "tickets");
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }

    private static void RestrictDirectory(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception) when (!OperatingSystem.IsWindows()) { }
    }

    private static void RestrictFile(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) when (!OperatingSystem.IsWindows()) { }
    }
}
