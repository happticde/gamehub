using GameHub.Shared;

namespace GameHub.Ingest.Data;

/// <summary>Encrypted platform authentication material owned by one Ingest tenant.</summary>
public sealed class PlatformCredential : IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Platform Platform { get; set; }
    public int CredentialSchemaVersion { get; set; }
    public int EncryptionFormatVersion { get; set; }
    public int EncryptionKeyVersion { get; set; }
    public byte[] EncryptedPayload { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
