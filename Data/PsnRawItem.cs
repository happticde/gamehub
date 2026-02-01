using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Data;

[Index(nameof(TitleId), IsUnique = true)] // <--- Enforces 1 row per game
public class PsnRawItem
{
    public int Id { get; set; }

    [Required]
    public string TitleId { get; set; } = string.Empty; // e.g. "CUSA00001_00"

    public string? TitleName { get; set; }

    // We store the FULL JSON so we can change our mind later about what data matters
    public string RawJson { get; set; } = "{}";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
