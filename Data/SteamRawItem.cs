using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Data;

[Index(nameof(AppId), IsUnique = true)]
public class SteamRawItem
{
    public int Id { get; set; }

    [Required]
    public int AppId { get; set; } // Steam uses integers (e.g. 220 for HL2)

    public string? Name { get; set; }

    public string RawJson { get; set; } = "{}";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
