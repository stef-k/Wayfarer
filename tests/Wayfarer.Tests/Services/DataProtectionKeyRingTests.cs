using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises deterministic path authority and actual key generation without touching installed rings.</summary>
public sealed class DataProtectionKeyRingTests
{
    /// <summary>Current, previous, ambiguous and explicit authorities select in place without moving files.</summary>
    [Fact]
    public void Selection_PreservesPreviousAndRejectsAmbiguity()
    {
        using var directory = new TestDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.Path, EnvironmentName = "Development" });
        builder.Configuration["Storage:DataRoot"] = directory.Path;
        var current = Path.Combine(directory.Path, "data-protection");
        var previous = Directory.CreateDirectory(Path.Combine(directory.Path, "previous")).FullName;
        DataProtectionKeyRing Resolve() => DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment, previous);
        Assert.Equal(new(current, "current Storage default"), Resolve());
        File.WriteAllText(Path.Combine(previous, "unrelated.xml"), "not a key");
        Assert.Equal(current, Resolve().Path);
        File.WriteAllText(Path.Combine(previous, "key-fixture.xml"), "opaque");
        Assert.Equal(new(previous, "previous-default compatibility"), Resolve());
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "key-fixture.xml"), "opaque");
        Assert.Throws<InvalidOperationException>(() => Resolve());
        builder.Configuration["DataProtection:KeyRingPath"] = current;
        Assert.Equal(new(current, "explicit override"), Resolve());
        Assert.True(File.Exists(Path.Combine(previous, "key-fixture.xml")));
    }

    /// <summary>Production defaults come from committed Storage configuration and Development remains platform-native.</summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Defaults_FollowStorage(string environment)
    {
        using var directory = new TestDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.Path, EnvironmentName = environment });
        if (environment == "Production")
            builder.Configuration.AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../appsettings.Production.json")));
        var ring = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment, Path.Combine(directory.Path, "absent"));
        if (environment == "Production") Assert.Equal("/var/lib/wayfarer/data-protection", ring.Path);
        else
        {
            Assert.True(Path.IsPathFullyQualified(ring.Path));
            Assert.EndsWith(Path.Combine("Wayfarer", "data-protection"), ring.Path);
            Assert.DoesNotContain(directory.Path, ring.Path, StringComparison.Ordinal);
        }
    }

    /// <summary>A new Storage default can generate usable keys while read-only registration leaves bytes unchanged.</summary>
    [Fact]
    public async Task CurrentDefault_GeneratesKeysAndReadOnlyHostDoesNotModifyThem()
    {
        using var directory = new TestDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.Path, EnvironmentName = "Development" });
        builder.Configuration["Storage:DataRoot"] = directory.Path;
        var selected = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment, Path.Combine(directory.Path, "absent"));
        builder.AddWayfarerDataProtection(selected);
        await using var host = builder.Build();
        var ring = host.Services.GetRequiredService<DataProtectionKeyRing>();
        Assert.Equal(Path.Combine(directory.Path, "data-protection"), ring.Path);
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture");
        var payload = protector.Protect("ready");
        var before = Directory.GetFiles(ring.Path).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        Assert.NotEmpty(before);
        var readOnly = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.Path, EnvironmentName = "Development" });
        readOnly.Configuration["Storage:DataRoot"] = directory.Path;
        readOnly.AddWayfarerDataProtection(selected, readOnlyKeys: true);
        await using var reader = readOnly.Build();
        Assert.True(reader.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Unprotect(payload) == "ready");
        Assert.Equal(before.Count, Directory.GetFiles(ring.Path).Length);
        foreach (var file in Directory.GetFiles(ring.Path)) Assert.True(before[Path.GetFileName(file)].SequenceEqual(File.ReadAllBytes(file)));
    }
}
