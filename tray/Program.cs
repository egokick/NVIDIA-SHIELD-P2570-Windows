using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ShieldController;

internal static class Program
{
    private static string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..20];
    private static string PipeName => "ShieldController-" + Identity;
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string checkpoint = "argument-validation";
        try
        {
            if (args.SequenceEqual(new[] { "--selftest" })) return SelfTests.Run();
            if (args.Length is 4 or 6 && args[0] == "--render-preview-state" && args[2] == "--output")
            {
                if (!Enum.TryParse<PreviewScene>(args[1], true, out var state) || !Enum.IsDefined(state)) throw new ArgumentException("Unknown preview state.");
                string path = Path.GetFullPath(args[3]);
                if (!path.Split(Path.DirectorySeparatorChar).Contains("debug", StringComparer.OrdinalIgnoreCase) || Path.GetExtension(path) != ".png") throw new ArgumentException("Preview output must be a PNG under debug.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Size? size = null;
                if (args.Length == 6)
                {
                    string[] dimensions = args[5].Split('x');
                    if (args[4] != "--viewport" || dimensions.Length != 2 || !int.TryParse(dimensions[0], out int w) || !int.TryParse(dimensions[1], out int h) || w is < 440 or > 3840 || h is < 480 or > 2160) throw new ArgumentException("Invalid preview viewport.");
                    size = new Size(w, h);
                }
                using var preview = new MainForm(preview: true);
                preview.RenderPreview(state, path, size);
                return 0;
            }
            if (args.Length > 1 || args.Length == 1 && args[0] is not ("--show" or "--exit")) throw new ArgumentException("Use --show or --exit.");
            checkpoint = "single-instance";
            using var mutex = new Mutex(true, @"Local\" + PipeName, out bool first);
            if (!first)
            {
                checkpoint = "existing-instance-command";
                return Send(args.Length == 1 && args[0] == "--exit" ? "exit" : "show");
            }
            if (args.SequenceEqual(new[] { "--exit" })) return 0;
            checkpoint = "create-window";
            using var form = new MainForm();
            _ = form.Handle; // IPC can enqueue work before Application.Run shows the window.
            using var cancellation = new CancellationTokenSource();
            Task server = StartServer(form, cancellation.Token, PipeName);
            checkpoint = "window-message-loop";
            Application.Run(form);
            checkpoint = "stop-command-server";
            cancellation.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            mutex.ReleaseMutex();
            return 0;
        }
        catch (Exception error)
        {
            if (args.Contains("--selftest") || args.Contains("--render-preview-state") || args.Contains("--exit"))
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = "tray-error", checkpoint, type = error.GetType().FullName, hresult = $"0x{error.HResult:X8}", stack_trace = error.StackTrace }));
                return 1;
            }
            string? log = CrashDiagnostics.Record(error, checkpoint);
            string description = checkpoint == "window-message-loop"
                ? "The controller application stopped unexpectedly."
                : "The controller application could not open.";
            MessageBox.Show(description + (log is null ? "\n\nThe diagnostic log could not be written." : "\n\nDiagnostic details were saved to:\n" + log), "NVIDIA SHIELD P2570", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static int Send(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(3000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, true); writer.WriteLine(command); writer.Flush();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, true);
        string response = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() ?? "";
        if (!int.TryParse(response, out int pid) || pid <= 0) return 1;
        if (command == "exit")
        {
            try { using var existing = System.Diagnostics.Process.GetProcessById(pid); return existing.WaitForExit(50000) ? 0 : 1; }
            catch (ArgumentException) { return 0; }
        }
        return 0;
    }
    internal static Task StartServer(MainForm form, CancellationToken cancellation, string name) => Task.Run(() => Serve(form, cancellation, name));
    private static async Task Serve(MainForm form, CancellationToken cancellation, string name)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, true);
                string? command = await reader.ReadLineAsync(cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
                if (command is not ("show" or "exit")) continue;
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, true);
                await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false); await writer.FlushAsync(cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested || form.IsDisposed) break;
                if (command == "show") form.BeginInvoke(form.ShowWindow);
                else form.BeginInvoke(async () => await form.ExitAsync());
            }
            catch (TimeoutException) { }
            catch (IOException) { } // An interrupted secondary instance must not kill the tray server.
            catch (InvalidOperationException) when (cancellation.IsCancellationRequested || form.IsDisposed) { }
        }
    }
}

internal static class SelfTests
{
    internal static int Run()
    {
        int checks = 0;
        checks += PairingFlowTests.Run();
        void Check(bool success) { checks++; if (!success) throw new InvalidOperationException("UI state check failed."); }
        Capabilities Paired(bool yes) => new(yes, false, [], [], [], null, null);
        var m = new StatusModel(); Check(m.State == ControllerState.NotPaired);
        m.ApplyCapabilities(Paired(false)); Check(!m.Paired);
        using var ready = JsonDocument.Parse("{\"transport_ready\":true,\"connected\":false}");
        using var pad = JsonDocument.Parse("{\"transport_ready\":true,\"connected\":true}");
        m.ApplySession(pad.RootElement); Check(m.State == ControllerState.NotPaired);
        m.BeginPairing(); Check(m.State == ControllerState.Pairing && !m.Paired);
        m.PairSucceeded(); Check(m.State == ControllerState.PairedDisconnected && m.Paired);
        m.ApplySession(ready.RootElement); Check(m.State == ControllerState.PairedDisconnected);
        m.ApplySession(pad.RootElement); Check(m.State == ControllerState.Connected);
        m.ApplySession(ready.RootElement); Check(m.State == ControllerState.PairedDisconnected);
        m.SetDisconnected(); Check(!m.TransportReady);
        m.ApplyCapabilities(new(true, true, [], [], [], null, null)); Check(m.State == ControllerState.Error);
        int events = 0, failures = 0; bool rejected = false;
        using var malformed = new StringReader("{\"phase\":\"first\"}\ninvalid\n{\"phase\":\"after_error\"}\n");
        try { OwnerClient.ConsumeEvents(malformed, _ => events++, () => failures++).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && events == 1 && failures == 1 && malformed.ReadLine() is null);
        using var callbackFails = new StringReader("{}\n{}\n"); failures = 0; rejected = false;
        try { OwnerClient.ConsumeEvents(callbackFails, _ => throw new InvalidOperationException(), () => failures++).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && failures == 1 && callbackFails.ReadLine() is null);
        Check(MainForm.RequirementSummary(new(false, false, [new("npcap", new string('x', 1000), false)], [], [], null, null)) == "Needs setup: Npcap");
        using (var form = new MainForm(preview: true))
        using (var cancellation = new CancellationTokenSource())
        {
            _ = form.Handle;
            var previous = SynchronizationContext.Current;
            var blockedContext = new CountingContext();
            SynchronizationContext.SetSynchronizationContext(blockedContext);
            try
            {
                Task server = Program.StartServer(form, cancellation.Token, "ShieldController-test-" + Guid.NewGuid().ToString("N"));
                cancellation.CancelAfter(50);
                try { server.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Check(cancellation.IsCancellationRequested && blockedContext.Posts == 0);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks, hardware_calls = 0 }));
        return 0;
    }
    private sealed class CountingContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    }
}
