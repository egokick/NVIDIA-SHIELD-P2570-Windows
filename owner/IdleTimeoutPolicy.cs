using Windows.Foundation;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    // The documented timeout switch is static/global, not a per-session lease.
    // Use only in this exclusive owned window and restore the pre-start value.
    private sealed class IdleTimeoutPolicy(bool original)
    {
        internal static IdleTimeoutPolicy ForRecovery(bool original, bool disableWasPlanned) => new(original) { DisableAttempted = disableWasPlanned };
        internal bool Original { get; } = original;
        internal bool DisableAttempted { get; private set; }
        internal bool DisabledObserved { get; private set; }
        internal bool RestoreAttempted { get; private set; }
        internal bool Restored { get; private set; }
        internal bool Pending { get; private set; }
        internal bool? FinalEnabled { get; private set; }
        private Task<bool>? pendingTask;

        private static async Task<bool> Complete(Task task) { await task; return true; }
        private async Task Apply(bool enabled, TimeSpan timeout)
        {
            IAsyncAction operation = enabled
                ? NetworkOperatorTetheringManager.EnableNoConnectionsTimeoutAsync()
                : NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();
            pendingTask = Complete(operation.AsTask());
            Pending = true;
            try { await pendingTask.WaitAsync(timeout); Pending = false; }
            catch
            {
                var late = await CancelAndSettle(() => operation.Cancel(), pendingTask, TimeSpan.FromSeconds(2));
                Pending = late.Pending;
                Log("idle-policy-cancel-settlement", new { requested_enabled = enabled, pending = Pending, cancel_call_failed = late.CancelCallFailed });
                if (!late.HasResult) throw;
            }
        }

        internal async Task DisableAfterOwnedStart(NetworkOperatorTetheringManager manager, bool owns)
        {
            if (!owns || manager.TetheringOperationalState != TetheringOperationalState.On)
                throw new InvalidOperationException("Owned active hotspot required for timeout change.");
            if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled() != Original)
                throw new InvalidOperationException("Timeout policy changed before owned mutation.");
            if (Original)
            {
                DisableAttempted = true; // Set before calling WinRT, including synchronous failure.
                await Apply(false, TimeSpan.FromSeconds(5));
            }
            DisabledObserved = !NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
            Log("idle-policy-disabled-verified", new { original_enabled = Original, setter_called = DisableAttempted, disabled_observed = DisabledObserved });
            if (!DisabledObserved) throw new InvalidOperationException("Timeout disable readback failed.");
        }

        internal async Task Restore()
        {
            try
            {
                // Never race an unsettled disable with a contradictory restore setter.
                if (Pending && pendingTask is not null)
                {
                    try { await pendingTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { }
                    Pending = !pendingTask.IsCompleted;
                }
                if (Pending) { Log("idle-policy-still-pending", new { restoration_proven = false }); return; }
                bool current = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
                if (DisableAttempted && current != Original)
                {
                    RestoreAttempted = true;
                    await Apply(Original, TimeSpan.FromSeconds(4));
                }
                FinalEnabled = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
                Restored = !Pending && FinalEnabled == Original;
            }
            catch (Exception ex)
            {
                Log("idle-policy-restore-error", new { type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}", pending = Pending });
                try { FinalEnabled = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled(); } catch { }
                Restored = !Pending && FinalEnabled == Original;
            }
            finally
            {
                Log("idle-policy-restoration", new { original_enabled = Original, disable_attempted = DisableAttempted, restore_attempted = RestoreAttempted, final_enabled = FinalEnabled, restored = Restored, operation_pending = Pending });
            }
        }
    }
}
