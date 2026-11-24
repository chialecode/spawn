using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Modules.Progress;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests;

public sealed class ProgressModuleSkeletonTests
{
    [Fact]
    public void AddSpawnProgress_RegistersWithoutError_AndFrozenContractsAreAccessible()
    {
        var services = new ServiceCollection();
        IServiceCollection returned = services.AddSpawnProgress();

        Assert.Same(services, returned);
        Assert.Equal("trainer.handshake", TrainerIpcMethods.Handshake);
        Assert.Equal(TrainerAudioMode.FullMute, new TrainerSettingsDto().AudioMode);
    }
}
