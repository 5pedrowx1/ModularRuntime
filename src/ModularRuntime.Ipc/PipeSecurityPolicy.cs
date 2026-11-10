using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ModularRuntime.Ipc;

/// <summary>
/// Access control of the daemon's named pipe.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The pipe DACL grants access only to the user running the daemon (no Everyone,
/// Authenticated Users, or Administrators entries).</item>
/// <item>An explicit deny entry for the NETWORK SID rejects remote (network logon) clients,
/// even for the same account.</item>
/// <item>The pipe is owned by the user SID, whether or not the daemon runs elevated. Before
/// sending anything, clients verify that owner (<see cref="VerifyServerOwner"/>), so a pipe
/// created first by another user (name squatting) is refused. An elevated and a non-elevated
/// process of the same user can therefore talk to each other; the built-in
/// <see cref="PipeOptions.CurrentUserOnly"/> check is not used because it compares against the
/// token's default owner, which is BUILTIN\Administrators in an elevated process.</item>
/// <item>The first server instance is created with <see cref="PipeOptions.FirstPipeInstance"/>,
/// so a daemon cannot attach to a pipe someone else already created.</item>
/// </list>
/// Knowing the pipe name grants nothing; any process running as the same Windows user can connect,
/// which is the documented trust boundary.
/// </remarks>
public static class PipeSecurityPolicy
{
    public static PipeSecurity CreateForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

        // Always the user SID (never BUILTIN\Administrators, the default owner of an elevated
        // token), so clients of the same user can verify the owner regardless of elevation.
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        return security;
    }

    internal static NamedPipeServerStream CreateServerStream(string pipeName, bool firstInstance, int maxInstances)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            CreateForCurrentUser());
    }

    internal static NamedPipeClientStream CreateClientStream(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    /// <summary>
    /// Verifies, right after connecting and before any data is sent, that the server's pipe is owned
    /// by the current user (or by the current token's default owner, which daemons before v0.3.0
    /// used when elevated).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The pipe belongs to someone else.</exception>
    internal static void VerifyServerOwner(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !(owner.Equals(identity.User) || owner.Equals(identity.Owner)))
        {
            throw new UnauthorizedAccessException("The daemon pipe is not owned by the current user; refusing to connect.");
        }
    }
}
