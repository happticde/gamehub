using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Data;

[Index(nameof(AppName), IsUnique = true)] // Epic uses 'AppName' (e.g. 'CodenameZ') as the ID
public class EpicRawItem
{
    public int Id { get; set; }

    [Required]
    public string AppName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string RawJson { get; set; } = "{}";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
