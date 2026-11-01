using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Controlled body progress cannot reset or monopolize the process-wide avatar slot.</summary>
public sealed class ProfilePhotoDeadlineTests
{
    /// <summary>Expire a progressing stream at 60 seconds, then prove next admission and the remaining helper budget.</summary>
    [Fact]
    public async Task OverallDeadlineStopsReadsAndReleasesSlotWithoutPublication()
    {
        using var storage = new PhotoTestStorage();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var settings = new Mock<IApplicationSettingsService>(); settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        var time = new DeadlineClock();
        var calls = 0;
        var upload = new ProfilePhotoUpload(service, settings.Object, time)
        {
            TransformAsync = (_, _, remaining, ct) =>
            {
                calls++; Assert.InRange(remaining.TotalSeconds, 4.9, 5.1); Assert.False(ct.IsCancellationRequested);
                return Task.FromResult(new ImageTransformResult(ImageTransformStatus.Unsupported));
            }
        };
        using var stream = new SlowMultipart();
        var request = new DefaultHttpContext().Request;
        request.ContentType = "multipart/form-data; boundary=photo-boundary";
        request.Body = stream;
        var operation = upload.UploadAsync(request, "owner", default);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(60));
        var timeout = await Assert.ThrowsAsync<ProfilePhotoException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("photo_upload_timeout", timeout.Code); Assert.Equal(0, calls);
        Assert.True(stream.Cancelled); Assert.False(Directory.Exists(storage.Store.Root));
        using var body = new MultipartFormDataContent("photo-boundary");
        body.Add(new StringContent("S0"), "expectedStateToken"); body.Add(new ByteArrayContent("input"u8.ToArray()), "photo", "photo.png");
        var encoded = await body.ReadAsByteArrayAsync();
        request.Body = new OnFirstRead(encoded, () => time.Advance(TimeSpan.FromSeconds(55)));
        var next = await Assert.ThrowsAsync<ProfilePhotoException>(() => upload.UploadAsync(request, "owner", default));
        Assert.Equal("unsupported_photo_format", next.Code); Assert.Equal(1, calls);
    }

    /// <summary>The stream advances headers and bytes, then waits cancellably rather than reaching EOF.</summary>
    private sealed class SlowMultipart : MemoryStream
    {
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled;
        internal SlowMultipart() : base(Encoding.ASCII.GetBytes("--photo-boundary\r\nContent-Disposition: form-data; name=\"photo\"; filename=\"photo.png\"\r\n\r\nbytes")) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
    }

    private sealed class OnFirstRead(byte[] bytes, Action advance) : MemoryStream(bytes)
    {
        private bool first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (first) { first = false; advance(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Only the ordinary CancellationTokenSource timer is controlled; no real-minute sleeps.</summary>
    private sealed class DeadlineClock : TimeProvider
    {
        private long timestamp;
        private DeadlineTimer? timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new DeadlineTimer(callback, state, timestamp + dueTime.Ticks);
        internal void Advance(TimeSpan elapsed)
        {
            timestamp += elapsed.Ticks;
            if (timer is { Disposed: false } && timestamp >= timer.Due) timer.Callback(timer.State);
        }
        private sealed class DeadlineTimer(TimerCallback callback, object? state, long due) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = due;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
