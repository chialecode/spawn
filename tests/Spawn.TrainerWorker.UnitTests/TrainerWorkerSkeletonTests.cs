using Spawn.Contracts;
using Xunit;

namespace Spawn.TrainerWorker.UnitTests;

public sealed class TrainerWorkerSkeletonTests
{
    [Fact]
    public void TestStubBinary_IsCopiedToTestOutput_AndIpcMethodConstantsAreFrozen()
    {
        string stubExe = Path.Combine(AppContext.BaseDirectory, "TestStub", "Spawn.TrainerWorker.TestStub.exe");
        Assert.True(File.Exists(stubExe), $"Expected TestStub executable at {stubExe}");

        Assert.Equal("trainer.launch", TrainerIpcMethods.Launch);
        Assert.Equal("trainer.triggerHotkey", TrainerIpcMethods.TriggerHotkey);
        Assert.Equal("trainer.setWindowVisibility", TrainerIpcMethods.SetWindowVisibility);
        Assert.Equal("trainer.setAudioFallback", TrainerIpcMethods.SetAudioFallback);
        Assert.Equal("trainer.addDefenderExclusion", TrainerIpcMethods.AddDefenderExclusion);
        Assert.Equal("trainer.stop", TrainerIpcMethods.Stop);
        Assert.Equal("trainer.emergencyShutdown", TrainerIpcMethods.EmergencyShutdown);
        Assert.Equal("trainer.processCreated", TrainerIpcMethods.ProcessCreated);
        Assert.Equal("trainer.sessionEnded", TrainerIpcMethods.SessionEnded);
    }
}
