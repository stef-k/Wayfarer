using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models.Enums;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Portable serialized references, bounded legacy reads, privacy and non-overlapping storage totals.</summary>
public sealed class LocationImportStagedFilesTests
{
    [Theory]
    [InlineData(LocationImportFileType.Csv, ".csv")]
    [InlineData(LocationImportFileType.Gpx, ".gpx")]
    [InlineData(LocationImportFileType.Kml, ".kml")]
    [InlineData(LocationImportFileType.GoogleTimeline, ".json")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, ".geojson")]
    public void NewReference_IsCanonicalAndResolvesBeneathDurableUploads(LocationImportFileType type, string extension)
    {
        var reference = LocationImportStagedFiles.CreateReference(type);
        Assert.Matches(@"^imports/[0-9a-f]{32}" + System.Text.RegularExpressions.Regex.Escape(extension) + "$", reference);
        Assert.DoesNotContain('\\', reference);
        Assert.True(ImportStaging.Files.TryResolve(reference, out var path));
        Assert.Equal(Path.Combine(ImportStaging.Files.DirectoryPath, reference[8..]), path);
        Assert.Equal(reference[8..], LocationImportStagedFiles.DisplayName(reference));
    }

    [Theory]
    [InlineData("imports\\7f4f05bc5a6e47de9e54d3a0194c12a1.csv")]
    [InlineData("imports/../7f4f05bc5a6e47de9e54d3a0194c12a1.csv")]
    [InlineData("imports//7f4f05bc5a6e47de9e54d3a0194c12a1.csv")]
    [InlineData("imports/./7f4f05bc5a6e47de9e54d3a0194c12a1.csv")]
    [InlineData("imports/7F4F05BC5A6E47DE9E54D3A0194C12A1.csv")]
    [InlineData("imports/7f4f05bc5a6e47de9e54d3a0194c12a1.exe")]
    [InlineData("imports/private.csv")]
    [InlineData("imports/7f4f05bc5a6e47de9e54d3a0194c12a1.csv\n")]
    [InlineData("C:\\private\\history.csv")]
    [InlineData("C:/private/history.csv")]
    [InlineData("\\\\server\\share\\history.csv")]
    [InlineData("//server/share/history.csv")]
    [InlineData("/private/history.csv")]
    [InlineData("")]
    public void UnsafeReferences_FailClosed(string reference) =>
        Assert.False(ImportStaging.Files.TryResolve(reference, out _));

    [Fact]
    public void LegacyResolution_IsBoundedToKnownRootsAndDoesNotRewrite()
    {
        foreach (var root in ImportStaging.Files.LegacyRoots)
        {
            var reference = Path.Combine(root, "old.csv");
            Assert.True(ImportStaging.Files.TryResolve(reference, out var path));
            Assert.Equal(reference, path);
            Assert.False(ImportStaging.Files.TryResolve(Path.Combine(root + "-sibling", "old.csv"), out _));
            Assert.False(ImportStaging.Files.TryResolve(Path.Combine(root, "..", "outside.csv"), out _));
        }
    }

    [Theory]
    [InlineData("imports/7f4f05bc5a6e47de9e54d3a0194c12a1.csv", "7f4f05bc5a6e47de9e54d3a0194c12a1.csv")]
    [InlineData("/private/source/history.csv", "history.csv")]
    [InlineData("C:\\private\\source\\history.csv", "history.csv")]
    [InlineData("\\\\server\\private\\history.csv", "history.csv")]
    [InlineData("C:private.csv", "Unavailable file")]
    [InlineData("/private/..", "Unavailable file")]
    [InlineData("/private/", "Unavailable file")]
    public void DisplayName_IsCrossPlatformAndBounded(string reference, string expected) =>
        Assert.Equal(expected, LocationImportStagedFiles.DisplayName(reference));

    [Fact]
    public void ReportingRoots_DeduplicateIdenticalAndNestedAuthorities()
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.ContentRootPath).Returns(AppContext.BaseDirectory);
        var storage = new StoragePaths(Options.Create(new StorageOptions
        {
            DataRoot = Path.Combine(AppContext.BaseDirectory, "Uploads", "Temp"),
            CacheRoot = Path.GetTempPath(), LogRoot = Path.GetTempPath(), TempRoot = Path.GetTempPath()
        }), host.Object);
        var files = new LocationImportStagedFiles(storage, host.Object);
        Assert.Single(files.LegacyRoots);
        Assert.Equal(files.LegacyRoots, files.ReportingRoots());
    }
}
