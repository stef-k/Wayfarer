using System;
using System.Text.Json;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Tests for GroupSseEventDto factory methods and JSON serialization.
/// </summary>
public class GroupSseEventDtoTests
{
    [Fact]
    public void Location_CreatesCorrectPayload()
    {
        var timestamp = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var dto = GroupSseEventDto.Location(
            locationId: 123,
            timestampUtc: timestamp,
            userId: "user-abc",
            userName: "john",
            isLive: true,
            locationType: "check-in");

        Assert.Equal("location", dto.Type);
        Assert.Equal(123, dto.LocationId);
        Assert.Equal(timestamp, dto.TimestampUtc);
        Assert.Equal("user-abc", dto.UserId);
        Assert.Equal("john", dto.UserName);
        Assert.True(dto.IsLive);
        Assert.Equal("check-in", dto.LocationType);
    }

    [Fact]
    public void Location_SerializesToCorrectJson()
    {
        var timestamp = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var dto = GroupSseEventDto.Location(123, timestamp, "user-abc", "john", true, "check-in");

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("location", root.GetProperty("type").GetString());
        Assert.Equal(123, root.GetProperty("locationId").GetInt32());
        Assert.Equal("user-abc", root.GetProperty("userId").GetString());
        Assert.Equal("john", root.GetProperty("userName").GetString());
        Assert.True(root.GetProperty("isLive").GetBoolean());
        Assert.Equal("check-in", root.GetProperty("locationType").GetString());
    }

    [Fact]
    public void Location_WithoutLocationType_OmitsFieldInJson()
    {
        var dto = GroupSseEventDto.Location(1, DateTime.UtcNow, "u", "n", false, null);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("locationType", out _));
    }

    [Fact]
    public void LocationDeleted_CreatesCorrectPayload()
    {
        var dto = GroupSseEventDto.LocationDeleted(123, "user-abc");

        Assert.Equal("location-deleted", dto.Type);
        Assert.Equal(123, dto.LocationId);
        Assert.Equal("user-abc", dto.UserId);
    }

    [Fact]
    public void LocationDeleted_SerializesToCorrectJson()
    {
        var dto = GroupSseEventDto.LocationDeleted(456, "user-xyz");

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("location-deleted", root.GetProperty("type").GetString());
        Assert.Equal(456, root.GetProperty("locationId").GetInt32());
        Assert.Equal("user-xyz", root.GetProperty("userId").GetString());
    }

    [Fact]
    public void VisibilityChanged_CreatesCorrectPayload()
    {
        var dto = GroupSseEventDto.VisibilityChanged("user-123", disabled: true);

        Assert.Equal("visibility-changed", dto.Type);
        Assert.Equal("user-123", dto.UserId);
        Assert.True(dto.Disabled);
    }

    [Fact]
    public void VisibilityChanged_SerializesToCorrectJson()
    {
        var dto = GroupSseEventDto.VisibilityChanged("user-123", true);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("visibility-changed", root.GetProperty("type").GetString());
        Assert.Equal("user-123", root.GetProperty("userId").GetString());
        Assert.True(root.GetProperty("disabled").GetBoolean());
    }

    [Fact]
    public void MemberJoined_CreatesCorrectPayload()
    {
        var invitationId = Guid.NewGuid();
        var dto = GroupSseEventDto.MemberJoined("user-new", invitationId);

        Assert.Equal("member-joined", dto.Type);
        Assert.Equal("user-new", dto.UserId);
        Assert.Equal(invitationId, dto.InvitationId);
    }

    [Fact]
    public void MemberJoined_SerializesToCorrectJson()
    {
        var invitationId = Guid.NewGuid();
        var dto = GroupSseEventDto.MemberJoined("user-new", invitationId);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("member-joined", root.GetProperty("type").GetString());
        Assert.Equal("user-new", root.GetProperty("userId").GetString());
        Assert.Equal(invitationId.ToString(), root.GetProperty("invitationId").GetString());
    }

    [Fact]
    public void MemberLeft_CreatesCorrectPayload()
    {
        var dto = GroupSseEventDto.MemberLeft("user-leaving");

        Assert.Equal("member-left", dto.Type);
        Assert.Equal("user-leaving", dto.UserId);
    }

    [Fact]
    public void MemberRemoved_CreatesCorrectPayload()
    {
        var dto = GroupSseEventDto.MemberRemoved("user-removed");

        Assert.Equal("member-removed", dto.Type);
        Assert.Equal("user-removed", dto.UserId);
    }

    [Fact]
    public void InviteCreated_CreatesCorrectPayload()
    {
        var invitationId = Guid.NewGuid();
        var dto = GroupSseEventDto.InviteCreated(invitationId);

        Assert.Equal("invite-created", dto.Type);
        Assert.Equal(invitationId, dto.InvitationId);
        Assert.Null(dto.UserId);
    }

    [Fact]
    public void InviteCreated_SerializesToCorrectJson()
    {
        var invitationId = Guid.NewGuid();
        var dto = GroupSseEventDto.InviteCreated(invitationId);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("invite-created", root.GetProperty("type").GetString());
        Assert.Equal(invitationId.ToString(), root.GetProperty("invitationId").GetString());
        Assert.False(root.TryGetProperty("userId", out _));
    }

    [Fact]
    public void InviteDeclined_CreatesCorrectPayload()
    {
        var invitationId = Guid.NewGuid();
        var dto = GroupSseEventDto.InviteDeclined("user-declining", invitationId);

        Assert.Equal("invite-declined", dto.Type);
        Assert.Equal("user-declining", dto.UserId);
        Assert.Equal(invitationId, dto.InvitationId);
    }

    [Fact]
    public void InviteRevoked_CreatesCorrectPayload()
    {
        var inviteId = Guid.NewGuid();
        var dto = GroupSseEventDto.InviteRevoked(inviteId);

        Assert.Equal("invite-revoked", dto.Type);
        Assert.Equal(inviteId, dto.InviteId);
        Assert.Null(dto.UserId);
    }

    [Fact]
    public void InviteRevoked_SerializesToCorrectJson()
    {
        var inviteId = Guid.NewGuid();
        var dto = GroupSseEventDto.InviteRevoked(inviteId);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("invite-revoked", root.GetProperty("type").GetString());
        Assert.Equal(inviteId.ToString(), root.GetProperty("inviteId").GetString());
        Assert.False(root.TryGetProperty("userId", out _));
    }
}
