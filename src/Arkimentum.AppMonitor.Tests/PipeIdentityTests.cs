using System.Security.Principal;
using Arkimentum.AppMonitor.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Tests;

/// <summary>
/// Identifying a pipe client must not leave the server's async flow impersonating that client. With
/// NamedPipeServerStream.RunAsClient on .NET 10 it did: the calling thread was reverted, but every continuation after
/// the next await, and every task started from them, ran as the client. In the SYSTEM service that produced "access
/// denied" on its own files and installers launched into the user's session. This test fails against that code.
/// </summary>
public class PipeIdentityTests
{
    private static string? Impersonating()
    {
        using var id = WindowsIdentity.GetCurrent(ifImpersonating: true);
        return id?.Name;
    }

    [Fact]
    public async Task Identifying_a_client_does_not_leak_impersonation_into_the_async_flow()
    {
        var pipeName = "ArkimentumTest-" + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(NullLogger<PipeServer>.Instance, pipeName);
        var received = new TaskCompletionSource<(PipeClientConnection Conn, IpcMessage Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerThread = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterAwait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideTask = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.MessageReceived += async (conn, message) =>
        {
            if (message is not GetStateMessage) return;
            handlerThread.TrySetResult(Impersonating());
            await Task.Yield();
            afterAwait.TrySetResult(Impersonating());
            await Task.Run(() => insideTask.TrySetResult(Impersonating()));
            received.TrySetResult((conn, message));
        };
        server.Start();

        await using var client = new PipeClient(NullLogger<PipeClient>.Instance, pipeName)
        {
            HelloFactory = () => new HelloMessage { SessionId = 1, UserName = "test", AgentVersion = "test", ClientKind = IpcClientKind.Admin },
        };
        client.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!client.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(client.IsConnected, "the test client did not connect to the in-process pipe server");
        Assert.True(await client.SendAsync(new GetStateMessage()));

        var (conn, _) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The identity itself must still be established from the OS (same account as this test process).
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.User?.Value, conn.UserSid);
        Assert.False(string.IsNullOrEmpty(conn.UserName));

        Assert.Null(await handlerThread.Task);
        Assert.Null(await afterAwait.Task);
        Assert.Null(await insideTask.Task);
    }
}
