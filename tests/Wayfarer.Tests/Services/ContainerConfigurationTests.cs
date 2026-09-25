using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wayfarer.CommandLine;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves opt-in secrets, bounded CLI parsing and forwarded-header trust at stable seams.</summary>
public sealed class ContainerConfigurationTests
{
    /// <summary>Native configuration is unchanged unless a password file is explicitly selected.</summary>
    [Fact]
    public void PasswordFile_OverridesOnlyPassword_AndSanitizesFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "secret");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=fixture;Username=fixture;Password=native"
        }).Build();
        try
        {
            DatabaseSecret.Apply(configuration);
            Assert.Equal("native", new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection")).Password);
            File.WriteAllText(path, "protected-value\n");
            configuration["Database:PasswordFile"] = path;
            DatabaseSecret.Apply(configuration);
            var resolved = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
            Assert.Equal("protected-value", resolved.Password);
            Assert.Equal("fixture", resolved.Database);
            foreach (var invalid in new[] { Path.Combine(root, "missing"), root, path })
            {
                File.WriteAllText(path, "\n");
                configuration["Database:PasswordFile"] = invalid;
                var error = Assert.Throws<InvalidOperationException>(() => DatabaseSecret.Apply(configuration));
                Assert.DoesNotContain("protected-value", error.ToString());
                Assert.DoesNotContain(root, error.ToString());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Real forwarding middleware applies one trusted hop and ignores direct spoofing.</summary>
    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.11", true)]
    [InlineData("192.0.2.10", false)]
    public async Task Forwarding_RequiresConfiguredPeer(string peer, bool configured)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            services.Configure<ForwardedHeadersOptions>(options => TrustedProxyConfiguration.Apply(options,
                configured ? ["192.0.2.10"] : [], []))).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                await next(context);
            });
            app.UseForwardedHeaders();
            app.Run(context => context.Response.WriteAsync($"{context.Request.Scheme}|{context.Request.Host}|{context.Connection.RemoteIpAddress}"));
        })).StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://direct.test/");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "public.test");
        request.Headers.Add("X-Forwarded-For", "198.51.100.10");
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(configured && peer == "192.0.2.10" ? "https|public.test|198.51.100.10" : $"http|direct.test|{peer}",
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>Neither an all-address CIDR nor password argv is an accepted new interface.</summary>
    [Fact]
    public async Task UnsafeInputs_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Apply(new(), [], ["0.0.0.0/0"]));
        var error = new StringWriter();
        Assert.Equal(2, await LifecycleCli.RunAsync(["admin", "bootstrap", "admin", "secret-value"],
            new StringReader(""), new StringWriter(), error));
        Assert.DoesNotContain("secret-value", error.ToString());
    }
}
