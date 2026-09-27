using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Middleware;
using Wayfarer.Parsers;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>Proves the request policy without allocating large upload bodies.</summary>
public sealed class UserFileUploadMiddlewareTests
{
    /// <summary>Defaults/historical values clamp before checked conversion; invalid negatives fail closed.</summary>
    [Theory]
    [InlineData(int.MinValue, -1)]
    [InlineData(-2, -1)]
    [InlineData(-1, -1)]
    [InlineData(0, 100)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(102400, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Policy_ResolvesSafely(int configured, int effective)
    {
        Assert.Equal(effective, UploadRequestPolicy.EffectiveMiB(configured));
        Assert.Equal(Math.Max(0, effective) * 1024L * 1024, UploadRequestPolicy.EffectiveBytes(configured));
        var range = typeof(ApplicationSettings).GetProperty(nameof(ApplicationSettings.UploadSizeLimitMB))!
            .GetCustomAttributes(typeof(RangeAttribute), false).Cast<RangeAttribute>().Single();
        Assert.Equal(configured >= -1 && configured <= 100, range.IsValid(configured));
    }

    /// <summary>Unmarked routes cannot even resolve the settings dependency.</summary>
    [Fact]
    public async Task UnrelatedRoute_DoesNotResolveSettings()
    {
        var context = new DefaultHttpContext();
        var invoked = false;
        await new UserFileUploadMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.True(invoked);
    }

    /// <summary>Declared and unknown lengths get the effective ceiling before downstream form work.</summary>
    [Theory]
    [InlineData(0, null, 200, 100)]
    [InlineData(102400, null, 200, 100)]
    [InlineData(1, null, 200, 1)]
    [InlineData(1, 1048575L, 200, 1)]
    [InlineData(1, 1048576L, 200, 1)]
    [InlineData(1, 1048577L, 413, 100)]
    [InlineData(-1, null, 403, 100)]
    [InlineData(-2, null, 403, 100)]
    public async Task Upload_EnforcesPolicy(int configured, long? length, int status, int featureMiB)
    {
        var context = Context(configured);
        context.Request.ContentLength = length;
        var feature = new SizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var invoked = false;
        await new UserFileUploadMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status == 200, invoked);
        Assert.Equal(featureMiB * 1024L * 1024, feature.MaxRequestBodySize);
    }

    /// <summary>A supported host must supply an enforceable ceiling, including for chunked requests.</summary>
    [Theory]
    [InlineData(false, null, 503)]
    [InlineData(true, null, 503)]
    [InlineData(true, 104857600L, 503)]
    [InlineData(true, 1048576L, 200)]
    [InlineData(true, 512L, 200)]
    public async Task UnchangeableFeature_FailsClosedUnlessAlreadyStrict(bool present, long? limit, int status)
    {
        var context = Context(1);
        if (present) context.Features.Set<IHttpMaxRequestBodySizeFeature>(new SizeFeature { IsReadOnly = true, MaxRequestBodySize = limit });
        var invoked = false;
        await new UserFileUploadMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status == 200, invoked);
    }

    /// <summary>Stricter endpoint metadata remains effective even before MVC processes its filters.</summary>
    [Fact]
    public async Task EndpointLimit_IsNeverRaised()
    {
        var context = Context(1, new RequestSizeLimitAttribute(512));
        var feature = new SizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        await new UserFileUploadMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        Assert.Equal(512, feature.MaxRequestBodySize);
    }

    /// <summary>The absolute boundary rejects a declared oversize body without downstream reads.</summary>
    [Fact]
    public async Task GlobalLimit_RejectsDeclaredOversize()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new SizeFeature());
        context.Request.ContentLength = UploadRequestPolicy.MaximumRequestBytes + 1;
        await new DynamicRequestSizeMiddleware(_ => throw new Exception("Body work invoked")).InvokeAsync(context);
        Assert.Equal(413, context.Response.StatusCode);
    }

    /// <summary>Creates only the settings and metadata needed by the production middleware.</summary>
    private static DefaultHttpContext Context(int configured, params object[] metadata)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings { UploadSizeLimitMB = configured });
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(settings.Object).BuildServiceProvider()
        };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata.Prepend(new UserFileUploadAttribute())), "upload"));
        return context;
    }

    /// <summary>Models host capability; real stream enforcement is covered by the routed Kestrel test.</summary>
    private sealed class SizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; } = UploadRequestPolicy.MaximumRequestBytes;
    }
}
