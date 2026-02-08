using GameHub.Shared;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace GameHub.Core.Data;

[Index(nameof(Slug), IsUnique = true)]
public class UnifiedGame
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public List<LibraryItem> Items { get; set; } = [];
}

[Index(nameof(UnifiedGameId), nameof(Platform), IsUnique = true)]
public class LibraryItem
{
    public int Id { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    public Platform Platform { get; set; }
    public string ExternalId { get; set; } = "";
    public DateTime LastSynced { get; set; } = DateTime.UtcNow;
}
