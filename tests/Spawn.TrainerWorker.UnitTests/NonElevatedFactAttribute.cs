using Xunit;

namespace Spawn.TrainerWorker.UnitTests;

/// <summary>
/// A fact that needs a non-elevated test process. The worker refuses its test-only flags when elevated, so these
/// tests cannot run on elevated machines (hosted CI runners run as administrator); they are reported as skipped
/// with the reason instead of passing silently.
/// </summary>
public sealed class NonElevatedFactAttribute : FactAttribute
{
    public NonElevatedFactAttribute()
    {
        if (WorkerIntegrityHelper.IsCurrentProcessElevated())
        {
            Skip = "not-run: needs a non-elevated process (the worker refuses test flags when elevated).";
        }
    }
}
