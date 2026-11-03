using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Services;
using Wayfarer.Tests.Util;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>Actual Kestrel/MVC body consumption retains the strictest administrator, active host and endpoint ceilings.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ProfilePhotoRequestBoundaryTests(PostgresImportTestFixture fixture)
{
    /// <summary>A legal sub-MiB image may exceed the request cap through ordinary multipart overhead alone.</summary>
    [PostgresTheory]
    [InlineData(1, null, true, true, 413)]
    [InlineData(1, null, false, true, 200)]
    [InlineData(1, null, true, false, 413)]
    [InlineData(2, null, true, true, 200)]
    [InlineData(2, 524288L, true, true, 413)]
    [InlineData(100, 33554432L, true, true, 200)]
    public async Task UploadRetainsMinimumThroughMvc(int administratorMiB, long? hostLimit,
        bool boundaryImage, bool chunked, int expectedStatus)
    {
        var owner = await fixture.CreateUserAsync();
        using var files = new PhotoTestStorage();
        using var provider = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), files.Store,
            NullLogger<ProfilePhotoService>.Instance);
        var state = await photos.OwnerStateAsync(owner.Id, default);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings { UploadSizeLimitMB = administratorMiB });
        long? observedLength = -1;
        long? activeLimit = null;
        await using var db = fixture.CreateContext();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(files.Store.Root))!;
        await using var app = await IdentityRouteHost.StartAsync(db, root, services =>
        {
            services.AddSingleton(settings.Object);
            services.AddSingleton(files.Store);
            services.AddSingleton(photos);
        }, configureApp: host => host.UseMiddleware<DynamicRequestSizeMiddleware>(), configureAuthorized: host =>
        {
            // Supply the active host ceiling at upload-policy ingress, after the global compatibility ceiling.
            host.Use(async (context, next) =>
            {
                if (hostLimit.HasValue)
                    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = hostLimit;
                await next(context);
            });
            host.UseMiddleware<UserFileUploadMiddleware>();
            host.Use(async (context, next) =>
            {
                if (context.Request.Method == "POST")
                {
                    observedLength = context.Request.ContentLength;
                    await next(context);
                    activeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize;
                }
                else await next(context);
            });
        }, useKestrel: true);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var cookie = IdentityRouteHost.Cookie(app.Services, owner.Id);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var antiforgery = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgeryCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + antiforgeryCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", antiforgery);
        var image = await File.ReadAllBytesAsync(Path.Combine(ImageHelperSupervisorTests.Fixtures, "photo.jpg"));
        if (boundaryImage) image = PadJpeg(image, 1024 * 1024 - 100);
        using var form = new MultipartFormDataContent("photo-boundary");
        form.Add(new StringContent(state!.StateToken), "expectedStateToken");
        form.Add(new ByteArrayContent(image), "photo", "photo.jpg");
        var body = await form.ReadAsByteArrayAsync();
        Assert.True(image.Length < 1024 * 1024);
        Assert.Equal(boundaryImage, body.Length > 1024 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/profile-photo/me") { Content = form };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (chunked)
        {
            Assert.Null(observedLength);
            Assert.Equal(Math.Min(16 * 1024L * 1024,
                Math.Min(administratorMiB * 1024L * 1024, hostLimit ?? long.MaxValue)), activeLimit);
        }
        var saved = await photos.OwnerStateAsync(owner.Id, default);
        Assert.Equal(expectedStatus == 200, saved!.ProfilePhoto is not null);
        if (expectedStatus == 200)
            Assert.True(File.Exists(Path.Combine(files.Store.Root, saved.ProfilePhoto!.Version + ".webp")));
        else
        {
            Assert.Equal(state.StateToken, saved.StateToken);
            Assert.False(Directory.Exists(files.Store.Root));
        }
    }

    /// <summary>Legal JPEG comment segments enlarge an existing image without altering its encoded pixels.</summary>
    private static byte[] PadJpeg(byte[] source, int length)
    {
        using var output = new MemoryStream(length);
        output.Write(source.AsSpan(0, 2));
        var remaining = length - source.Length;
        while (remaining > 0)
        {
            var segment = Math.Min(remaining, 65537);
            if (remaining - segment is > 0 and < 4) segment -= 4;
            output.Write([0xff, 0xfe, (byte)((segment - 2) >> 8), (byte)(segment - 2)]);
            output.Write(new byte[segment - 4]);
            remaining -= segment;
        }
        output.Write(source.AsSpan(2));
        return output.ToArray();
    }
}
