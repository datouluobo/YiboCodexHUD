using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace YiboCodexHUD.Desktop.Services;

public static class SingleInstanceCommandClient
{
    private const int AsfwAny = -1;

    public static async Task<bool> TrySendLaunchOrFocusCodexAsync(TimeSpan timeout)
    {
        try
        {
            _ = AllowSetForegroundWindow(AsfwAny);

            using var timeoutSource = new CancellationTokenSource(timeout);
            await using var pipe = new NamedPipeClientStream(
                ".",
                SingleInstanceCommandProtocol.PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutSource.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(SingleInstanceCommandProtocol.LaunchOrFocusCodexCommand.AsMemory(), timeoutSource.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
