using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Owl1Client.Services;

/// <summary>서버 접속 정보(IP/Port)를 exe 옆의 settings.json에 저장/로드한다.</summary>
public class AppSettings
{
    public string ServerIp { get; set; } = "10.10.10.142";
    public int ServerPort { get; set; } = 6000;

    private static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] 설정 로드 실패: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] 설정 저장 실패: {ex.Message}");
        }
    }
}
