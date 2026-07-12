using System.Reflection;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Integration tests for tenant-owned custom tag editing and durable suggestions.</summary>
public sealed class CustomTagTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;

    public CustomTagTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpdateCustomTags_ReplacesOnlyCustomAssignmentsAndRetainsSuggestions()
    {
        var game = new UnifiedGame
        {
            Title = "Tagged game",
            Slug = "tagged-game",
            Genres = "RPG"
        };
        var automaticTag = new Tag { Name = "RPG" };
        var removedCustomTag = new Tag { Name = "Old favorite" };
        _db.AddRange(game, automaticTag, removedCustomTag);
        await _db.SaveChangesAsync();
        _db.Set<TagAssignment>().AddRange(
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = automaticTag.Id,
                IsCustom = false
            },
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = removedCustomTag.Id,
                IsCustom = true
            });
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = Assert.IsType<OkObjectResult>(await controller.UpdateCustomTags(
            game.Id,
            new CustomTagUpdateRequest
            {
                Tags = ["  Backlog   2026 ", "rpg", "BACKLOG 2026"]
            }));

        Assert.Equal(
            ["Backlog 2026", "RPG"],
            GetProperty<IEnumerable<string>>(result.Value!, "CustomTags"));

        _db.ChangeTracker.Clear();
        var assignments = await _db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .OrderBy(assignment => assignment.Tag!.Name)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, assignment => Assert.True(assignment.IsCustom));
        Assert.DoesNotContain(assignments, assignment => assignment.Tag!.Name == "Old favorite");

        await controller.UpdateCustomTags(
            game.Id,
            new CustomTagUpdateRequest { Tags = [] });

        _db.ChangeTracker.Clear();
        var remainingAssignment = await _db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .SingleAsync();
        Assert.Equal("RPG", remainingAssignment.Tag!.Name);
        Assert.False(remainingAssignment.IsCustom);
        Assert.Equal(
            ["Backlog 2026", "Old favorite", "RPG"],
            await _db.Tags.OrderBy(tag => tag.Name).Select(tag => tag.Name).ToListAsync());
    }

    private LibraryController CreateController() =>
        new(
            null!,
            null!,
            null!,
            _db,
            NullLogger<LibraryController>.Instance,
            new SyncSchedulerStatus(),
            new DefaultTenantContext(),
            new ActivityLogService(_db),
            null!);

    private static T GetProperty<T>(object instance, string propertyName) =>
        (T)(instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found."));
}
