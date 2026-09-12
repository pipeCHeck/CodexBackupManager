using System.Text.Json;

namespace CodexBackupManager.Backup.Checksums;

/// <summary><see cref="ChecksumManifest"/>의 JSON 직렬화 규칙(camelCase).</summary>
public static class ChecksumJson
{
    /// <summary>직렬화/역직렬화 공용 옵션.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>checksums.json 텍스트를 만든다.</summary>
    public static string Serialize(ChecksumManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    /// <summary>checksums.json 텍스트를 되돌린다.</summary>
    public static ChecksumManifest Deserialize(string json)
        => JsonSerializer.Deserialize<ChecksumManifest>(json, Options)
           ?? throw new JsonException("checksums.json이 null로 역직렬화되었습니다.");
}
