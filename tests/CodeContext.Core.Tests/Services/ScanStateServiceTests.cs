using CodeContext.Core.Services;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class ScanStateServiceTests
    {
        [Fact]
        public void InitialState_IsNotStarted()
        {
            var state = new ScanStateService();

            Assert.Equal(ScanPhase.NotStarted, state.Phase);
            Assert.Equal(0, state.FilesProcessed);
            Assert.Equal(0, state.FilesTotal);
            Assert.False(state.WatcherActive);
            Assert.Null(state.LastScanDuration);
            Assert.Null(state.LastError);
        }

        [Fact]
        public void TryBeginScan_FromNotStarted_TransitionsToScanning()
        {
            var state = new ScanStateService();

            Assert.True(state.TryBeginScan());
            Assert.Equal(ScanPhase.Scanning, state.Phase);
            Assert.NotNull(state.LastScanStartedAt);
        }

        [Fact]
        public void TryBeginScan_WhileScanning_ReturnsFalse()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            Assert.False(state.TryBeginScan());
        }

        [Fact]
        public void TryBeginScan_AfterReady_AllowsRescanAndResetsCounters()
        {
            var state = new ScanStateService();
            state.TryBeginScan();
            state.ReportProgress(5, 10, "a.cs");
            state.ReportComplete(10, TimeSpan.FromSeconds(2));

            Assert.True(state.TryBeginScan());
            Assert.Equal(ScanPhase.Scanning, state.Phase);
            Assert.Equal(0, state.FilesProcessed);
            Assert.Equal(0, state.FilesTotal);
        }

        [Fact]
        public void ReportProgress_UpdatesCounters()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            state.ReportProgress(3, 12, "file.cs");

            Assert.Equal(3, state.FilesProcessed);
            Assert.Equal(12, state.FilesTotal);
            Assert.Equal(ScanPhase.Scanning, state.Phase);
        }

        [Fact]
        public void ReportComplete_TransitionsToReady_WithDuration()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            state.ReportComplete(12, TimeSpan.FromSeconds(3));

            Assert.Equal(ScanPhase.Ready, state.Phase);
            Assert.Equal(12, state.FilesProcessed);
            Assert.Equal(TimeSpan.FromSeconds(3), state.LastScanDuration);
        }

        [Fact]
        public void ReportError_MarksGenerationIncomplete()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            state.ReportError("bad.cs", "parse failure");

            Assert.Equal(ScanPhase.Error, state.Phase);
            Assert.Contains("bad.cs", state.LastError);

            state.ReportComplete(0, TimeSpan.FromSeconds(1));
            Assert.Equal(ScanPhase.Error, state.Phase);
        }

        [Fact]
        public void FailScan_TransitionsToError()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            state.FailScan("boom");

            Assert.Equal(ScanPhase.Error, state.Phase);
            Assert.Equal("boom", state.LastError);
        }

        [Fact]
        public void CompleteScan_WhenScanning_TransitionsToReady()
        {
            var state = new ScanStateService();
            state.TryBeginScan();

            state.CompleteScan();

            Assert.Equal(ScanPhase.Ready, state.Phase);
            Assert.NotNull(state.LastScanDuration);
        }

        [Fact]
        public void CompleteScan_AfterFailure_DoesNotOverrideError()
        {
            var state = new ScanStateService();
            state.TryBeginScan();
            state.FailScan("boom");

            state.CompleteScan();

            Assert.Equal(ScanPhase.Error, state.Phase);
        }
    }
}
