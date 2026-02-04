using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Data;

[Index(nameof(GameId), IsUnique = true)]
public class GogRawItem
{
    public int Id { get; set; }

    [Required]
    public long GameId { get; set; } // GOG IDs are often long integers

    public string? Title { get; set; }

    public string RawJson { get; set; } = "{}";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
