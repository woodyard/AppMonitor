using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Arkimentum.AppMonitor.Cloud;

public sealed class DeviceCredential
{
    public required Guid OrganizationId { get; set; }
    public required Guid DeviceId { get; set; }
    public required string DeviceKey { get; set; }
    public required string ServerUrl { get; set; }
    public string? OrganizationName { get; set; }
    public DateTimeOffset EnrolledUtc { get; set; }
}

/// <summary>
/// Persists the device credential returned by enrollment, DPAPI-protected for the machine (LocalMachine scope) and
/// stored in a file that only SYSTEM and Administrators can read. Lives in the state directory (ProgramData).
/// </summary>
public sealed class DeviceCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Arkimentum.AppMonitor.DeviceCredential.v1");
    private readonly ILogger _logger;
    private readonly string _path;

    public DeviceCredentialStore(ILogger<DeviceCredentialStore> logger, string stateDirectory)
    {
        _logger = logger;
        _path = Path.Combine(stateDirectory, "device.credential");
    }

    public string Path_ => _path;

    public DeviceCredential? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var protectedBytes = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<DeviceCredential>(json, CloudJson.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device credential at {Path} could not be read; the device will re-enroll", _path);
            return null;
        }
    }

    public void Save(DeviceCredential credential)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential, CloudJson.Options);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.LocalMachine);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _path, overwrite: true);
        TryRestrictAcl(_path);
        _logger.LogInformation("Device credential stored at {Path} (device {DeviceId}, organization {OrganizationId})", _path, credential.DeviceId, credential.OrganizationId);
    }

    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", _path); }
    }

    private void TryRestrictAcl(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            // Outside the service (console / --user-config testing) the current user must keep access to what it wrote.
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem && identity.User is not null)
                    acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            fi.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not restrict the ACL of {Path}", path);
        }
    }
}
