using System;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

public class MobileDtoTests
{
    [Fact]
    public void GroupLocationsQueryResponse_ReportsMetadata()
    {
        var response = new GroupLocationsQueryResponse
        {
            TotalItems = 10,
            ReturnedItems = 3,
            PageSize = 3,
            HasMore = true,
            NextPageToken = "token",
            IsTruncated = true,
            Results = Array.Empty<PublicLocationDto>()
        };

        Assert.Equal(10, response.TotalItems);
        Assert.Equal(3, response.ReturnedItems);
        Assert.True(response.HasMore);
        Assert.Equal("token", response.NextPageToken);
        Assert.True(response.IsTruncated);
    }
}
