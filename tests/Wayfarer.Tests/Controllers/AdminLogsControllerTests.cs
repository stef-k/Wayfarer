using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the Admin LogsController which provides log file viewing functionality.
/// </summary>
public class AdminLogsControllerTests : TestBase, IDisposable
{
    private readonly string _tempLogDir;
    private readonly ApplicationDbContext _db;

    public AdminLogsControllerTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), $"wayfarer-logs-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempLogDir);
        _db = CreateDbContext();
    }

    public new void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempLogDir))
        {
            Directory.Delete(_tempLogDir, true);
        }
    }

    #region Index Tests

    [Fact]
    public void Index_ReturnsView_WithLogFileList()
    {
        // Arrange
        CreateTestLogFile("wayfarer-20251220.log", "Test log content line 1\nTest log content line 2");
        CreateTestLogFile("wayfarer-20251221.log", "Another log file content");
        var controller = BuildController();

        // Act
        var result = controller.Index();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<List<LogFileInfo>>(view.Model);
        Assert.Equal(2, model.Count);
        Assert.Contains(model, f => f.FileName == "wayfarer-20251220.log");
        Assert.Contains(model, f => f.FileName == "wayfarer-20251221.log");
    }

    [Fact]
    public void Index_ReturnsEmptyList_WhenNoLogFiles()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = controller.Index();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<List<LogFileInfo>>(view.Model);
        Assert.Empty(model);
    }

    [Fact]
    public void Index_SortsFilesByDateDescending()
    {
        // Arrange - Create files with explicit timestamps by setting LastWriteTime
        CreateTestLogFile("wayfarer-20251218.log", "Oldest");
        File.SetLastWriteTime(Path.Combine(_tempLogDir, "wayfarer-20251218.log"), DateTime.Now.AddDays(-3));

        CreateTestLogFile("wayfarer-20251220.log", "Newest");
        File.SetLastWriteTime(Path.Combine(_tempLogDir, "wayfarer-20251220.log"), DateTime.Now);

        CreateTestLogFile("wayfarer-20251219.log", "Middle");
        File.SetLastWriteTime(Path.Combine(_tempLogDir, "wayfarer-20251219.log"), DateTime.Now.AddDays(-1));

        var controller = BuildController();

        // Act
        var result = controller.Index();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<List<LogFileInfo>>(view.Model);
        Assert.Equal(3, model.Count);
        // Files should be ordered by LastModified descending (most recent first)
        Assert.Equal("wayfarer-20251220.log", model[0].FileName);
    }

    #endregion

    #region GetLogContent Tests

    [Fact]
    public async Task GetLogContent_ReturnsContent_ForValidFile()
    {
        // Arrange
        var content = "Line 1\nLine 2\nLine 3";
        CreateTestLogFile("wayfarer-20251222.log", content);
        var controller = BuildController();

        // Act
        var result = await controller.GetLogContent("wayfarer-20251222.log");

        // Assert
        var json = Assert.IsType<JsonResult>(result);
        var response = Assert.IsType<LogContentResponse>(json.Value);
        Assert.Equal(3, response.LineCount);
        Assert.Contains("Line 1", response.Content);
        Assert.Contains("Line 2", response.Content);
        Assert.Contains("Line 3", response.Content);
    }

    [Fact]
    public async Task GetLogContent_ReturnsFromPosition_ForIncrementalRead()
    {
        // Arrange - Use enough lines to ensure we can read incrementally
        var content = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8\nLine 9\nLine 10";
        CreateTestLogFile("wayfarer-20251222.log", content);
        var controller = BuildController();

        // Act - First read (2 lines)
        var result1 = await controller.GetLogContent("wayfarer-20251222.log", 0, 2);
        var json1 = Assert.IsType<JsonResult>(result1);
        var response1 = Assert.IsType<LogContentResponse>(json1.Value);

        // Assert first read
        Assert.Equal(2, response1.LineCount);
        Assert.Contains("Line 1", response1.Content);
        Assert.Contains("Line 2", response1.Content);
        Assert.True(response1.NewPosition > 0);

        // The HasMore check consumes an extra line, so we need to account for that
        // by reading from a fresh controller to avoid position issues
        var controller2 = BuildController();

        // Read all remaining content to verify there's more
        var result2 = await controller2.GetLogContent("wayfarer-20251222.log", response1.NewPosition, 100);
        var json2 = Assert.IsType<JsonResult>(result2);
        var response2 = Assert.IsType<LogContentResponse>(json2.Value);

        // Assert second read has remaining content
        Assert.True(response2.LineCount > 0);
    }

    [Fact]
    public async Task GetLogContent_ReturnsBadRequest_ForInvalidFileName()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = await controller.GetLogContent("../../../etc/passwd");

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetLogContent_ReturnsBadRequest_ForNonMatchingPattern()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = await controller.GetLogContent("malicious.exe");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetLogContent_ReturnsNotFound_ForMissingFile()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = await controller.GetLogContent("wayfarer-99991231.log");

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    #endregion

    #region DownloadLog Tests

    [Fact]
    public void DownloadLog_ReturnsFile_ForValidFileName()
    {
        // Arrange
        var content = "Download test content";
        CreateTestLogFile("wayfarer-20251222.log", content);
        var controller = BuildController();

        // Act
        var result = controller.DownloadLog("wayfarer-20251222.log");

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/octet-stream", fileResult.ContentType);
        Assert.Equal("wayfarer-20251222.log", fileResult.FileDownloadName);

        // Dispose the stream to release the file handle
        fileResult.FileStream.Dispose();
    }

    [Fact]
    public void DownloadLog_ReturnsBadRequest_ForInvalidFileName()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = controller.DownloadLog("../secret.txt");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadLog_ReturnsNotFound_ForMissingFile()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = controller.DownloadLog("wayfarer-99991231.log");

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    #endregion

    #region SearchLogs Tests

    [Fact]
    public async Task SearchLogs_ReturnsMatches_ForValidQuery()
    {
        // Arrange
        var content = "INFO: Application started\nERROR: Something failed\nINFO: Processing complete\nERROR: Another error";
        CreateTestLogFile("wayfarer-20251222.log", content);
        var controller = BuildController();

        // Act
        var result = await controller.SearchLogs("wayfarer-20251222.log", "ERROR");

        // Assert
        var json = Assert.IsType<JsonResult>(result);
        var jsonString = System.Text.Json.JsonSerializer.Serialize(json.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task SearchLogs_IsCaseInsensitive()
    {
        // Arrange
        var content = "Error occurred\nERROR happened\nerror found";
        CreateTestLogFile("wayfarer-20251222.log", content);
        var controller = BuildController();

        // Act
        var result = await controller.SearchLogs("wayfarer-20251222.log", "error");

        // Assert
        var json = Assert.IsType<JsonResult>(result);
        var jsonString = System.Text.Json.JsonSerializer.Serialize(json.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
        Assert.Equal(3, doc.RootElement.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task SearchLogs_ReturnsBadRequest_ForEmptyQuery()
    {
        // Arrange
        CreateTestLogFile("wayfarer-20251222.log", "content");
        var controller = BuildController();

        // Act
        var result = await controller.SearchLogs("wayfarer-20251222.log", "");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SearchLogs_ReturnsBadRequest_ForInvalidFileName()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = await controller.SearchLogs("../etc/passwd", "root");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    #endregion

    #region Security Tests

    [Theory]
    [InlineData("../wayfarer-20251222.log")]
    [InlineData("..\\wayfarer-20251222.log")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config\\SAM")]
    [InlineData("wayfarer-20251222.log.exe")]
    [InlineData("wayfarer-2025122.log")] // Wrong date format
    [InlineData("wayfarer-202512221.log")] // Too many digits
    [InlineData("other-20251222.log")] // Wrong prefix
    public async Task GetLogContent_RejectsMaliciousFileNames(string maliciousFileName)
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = await controller.GetLogContent(maliciousFileName);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a test log file in the temporary log directory.
    /// </summary>
    private void CreateTestLogFile(string fileName, string content)
    {
        var filePath = Path.Combine(_tempLogDir, fileName);
        File.WriteAllText(filePath, content);
    }

    /// <summary>
    /// Builds a LogsController with mocked dependencies.
    /// </summary>
    private LogsController BuildController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogFilePath:Default"] = Path.Combine(_tempLogDir, "wayfarer-.log")
            })
            .Build();

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var controller = new LogsController(
            NullLogger<LogsController>.Instance,
            _db,
            config,
            env.Object);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            }, "TestAuth"))
        };

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        return controller;
    }

    #endregion
}
