using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBackupManager.Backup.Manifest;

/// <summary><see cref="BackupManifest"/>의 JSON 직렬화 규칙(camelCase, 스펙: docs/codexbackup-format-v1.md §3.2).</summary>
public static class ManifestJson
{
    /// <summary>직렬화/역직렬화 공용 옵션.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>manifest를 JSON 문자열로 만든다.</summary>
    public static string Serialize(BackupManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    /// <summary>
    /// JSON 문자열을 manifest로 되돌린다. <c>backupFormatVersion</c> 같은 필수 필드가 없으면
    /// <see cref="JsonException"/>이 발생한다(<see cref="BackupManifest"/>의 <c>required</c> 표시).
    /// </summary>
    public static BackupManifest Deserialize(string json)
        => JsonSerializer.Deserialize<BackupManifest>(json, Options)
           ?? throw new JsonException("manifest.json이 null로 역직렬화되었습니다.");
}
