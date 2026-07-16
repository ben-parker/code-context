using System.Diagnostics;

namespace CodeContext.Core.Instances
{
    /// <summary>
    /// Validates that a PID still belongs to the instance a registry record describes.
    /// A PID alone can be reused by the OS; the process start-time fingerprint (when
    /// recorded) distinguishes the original process from an unrelated successor.
    /// </summary>
    public static class InstanceIdentity
    {
        // Start times from different APIs/serialization round-trips can differ by a tick
        // or a rounding step; anything under a few seconds is the same process.
        private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(3);

        public static bool TryGetProcessStartTime(int pid, out DateTimeOffset startTime)
        {
            startTime = default;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return false;
                startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// True when the record's PID is alive and, as far as the OS lets us verify,
        /// still refers to the process the record was written for.
        /// </summary>
        public static bool Matches(InstanceRecord record)
        {
            try
            {
                using var process = Process.GetProcessById(record.Pid);
                if (process.HasExited) return false;

                if (record.ProcessStartTime is not { } recordedStart)
                {
                    // Legacy PID-only records are not strong enough to authorize status
                    // access or a process-tree kill. They are pruned and can be recreated
                    // by the next start command with a full fingerprint.
                    return false;
                }

                try
                {
                    var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    return (actualStart - recordedStart).Duration() <= StartTimeTolerance;
                }
                catch
                {
                    // A recorded fingerprint is authoritative. If the OS will not let
                    // us verify it, fail closed rather than target a possibly reused PID.
                    return false;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
