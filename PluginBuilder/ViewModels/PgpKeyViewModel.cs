using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class PgpKeyViewModel
{
    public const int MaxArmouredPublicKeyLength = 256 * 1024;
    public const string PublicKeyTooLargeError = "GPG public key must not exceed 256 KiB.";

    public string? KeyId { get; set; }
    public string? Fingerprint { get; set; }

    [MaxLength(MaxArmouredPublicKeyLength, ErrorMessage = PublicKeyTooLargeError)]
    public string? PublicKey { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset AddedDate { get; set; }
    public long ValidDays { get; set; }
    public int Version { get; set; }
}

public record SignatureProofResponse(bool valid, string message, SignatureProof? proof = null);

public record UserKey(string PublicKeyArmored, string Fingerprint);

public class SignatureProof
{
    public string? Armour { get; set; }
    public string? KeyId { get; set; }
    public string? Fingerprint { get; set; }
    public DateTime SignedAt { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}
