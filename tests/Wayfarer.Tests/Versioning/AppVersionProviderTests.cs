using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class AppVersionProviderTests
{
    [Fact]
    public void Version_DefaultProviderReadsCompiledWayfarerVersion()
    {
        var provider = new AppVersionProvider();

        provider.Version.Should().Be("1.9.0");
    }

    [Fact]
    public void Version_ReadsAssemblyInformationalVersion()
    {
        var assembly = CreateAssemblyWithInformationalVersion("9.8.7-test");

        var provider = new AppVersionProvider(assembly);

        provider.Version.Should().Be("9.8.7-test");
    }

    [Fact]
    public void Version_CanUseMarkerTypeAssemblyInsteadOfEntryAssembly()
    {
        var provider = new AppVersionProvider(typeof(AppVersionProviderTests));
        var expectedVersion = typeof(AppVersionProviderTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        provider.Version.Should().Be(expectedVersion);
        Assert.NotSame(typeof(AppVersionProviderTests).Assembly, Assembly.GetEntryAssembly());
    }

    private static Assembly CreateAssemblyWithInformationalVersion(string version)
    {
        var attributeConstructor = typeof(AssemblyInformationalVersionAttribute)
            .GetConstructor(new[] { typeof(string) })!;
        var assemblyName = new AssemblyName($"WayfarerVersionTest{Guid.NewGuid():N}");
        var informationalVersion = new CustomAttributeBuilder(attributeConstructor, new object[] { version });

        return AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run,
            new[] { informationalVersion });
    }
}
