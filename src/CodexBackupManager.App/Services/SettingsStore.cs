using System;
using System.IO;
using System.Text.Json;

namespace CodexBackupManager.App.Services;

/// <summary>
/// <c>%APPDATA%\CodexBackupManager\settings.json</c> 읽기/쓰기.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    /// <summary>기본 경로로 생성한다.</summary>
    public SettingsStore()
        : this(AppPaths.SettingsFile)
    {
    }

    /// <summary>경로를 지정해 생성한다. (테스트용)</summary>
    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>설정 파일 경로.</summary>
    public string FilePath => _filePath;

    /// <summary>읽는다. 파일이 없거나 손상되면 기본값을 반환한다.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            using FileStream stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// 쓴다. 임시 파일에 쓰고 교체해서 중간에 실패해도 기존 설정이 깨지지 않게 한다.
    /// </summary>
    /// <returns>성공 여부.</returns>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _filePath + ".tmp";
            using (FileStream stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, settings, Options);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporary, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _filePath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
