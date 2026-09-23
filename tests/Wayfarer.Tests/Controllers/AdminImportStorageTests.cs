using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public partial class AdminSettingsControllerTests
{
    /// <summary>Admin reports durable staging and counts distinct legacy files without moving them.</summary>
    [Fact]
    public async Task Index_ReportsDurableAndLegacyUploadStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var files = ImportStaging.Create(root);
        Directory.CreateDirectory(files.DirectoryPath);
        Directory.CreateDirectory(files.LegacyRoots[0]);
        await File.WriteAllBytesAsync(Path.Combine(files.DirectoryPath, "new.csv"), new byte[1024 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(files.LegacyRoots[0], "legacy.csv"), new byte[1024 * 1024]);
        try
        {
            var (controller, _, _) = BuildController(files: files);
            await controller.Index();
            Assert.Equal(files.DirectoryPath, controller.ViewData["UploadsPath"]);
            Assert.Contains(files.LegacyRoots[0], Assert.IsType<string[]>(controller.ViewData["LegacyUploadsPaths"]));
            Assert.Equal(2, controller.ViewData["UploadsFileCount"]);
            Assert.Equal(2d, controller.ViewData["UploadsSizeMB"]);
            Assert.Equal(2d, controller.ViewData["CombinedStorageMB"]);
            Assert.True(File.Exists(Path.Combine(files.LegacyRoots[0], "legacy.csv")));
        }
        finally { Directory.Delete(root, true); }
    }
}
