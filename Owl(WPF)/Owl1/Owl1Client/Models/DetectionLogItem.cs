namespace Owl1Client.Models;

/// <summary>화면(로그 패널)에 표시하기 위해 가공된 로그 한 건.</summary>
public class DetectionLogItem
{
    public int LId { get; }
    public string Category { get; }
    public DateTime DateTime { get; }
    public string DisplayText { get; }

    /// <summary>true면 서버가 보낸 감지 로그(선택해서 사격/사격중단 가능).
    /// false면 사격승인/사격중단처럼 클라이언트가 버튼 클릭 시점에 직접 만들어
    /// 넣은 액션 로그로, 같은 l_id로 결정이 중복 전송되지 않도록 재선택이 막힌다.</summary>
    public bool IsSelectable { get; }

    public DetectionLogItem(int lId, string category, DateTime dateTime)
        : this(lId, category, dateTime, isActionLog: false)
    {
    }

    /// <summary>사격승인/사격중단처럼 서버 응답이 아니라 버튼을 누른 시점에
    /// 클라이언트가 직접 만들어 로그 패널에 넣는 액션 로그 항목을 생성한다.</summary>
    public static DetectionLogItem CreateActionLog(int lId, string actionLabel, DateTime dateTime)
        => new(lId, actionLabel, dateTime, isActionLog: true);

    private DetectionLogItem(int lId, string categoryOrLabel, DateTime dateTime, bool isActionLog)
    {
        LId = lId;
        Category = categoryOrLabel;
        DateTime = dateTime;
        IsSelectable = !isActionLog;
        DisplayText = isActionLog
            ? $"{dateTime:HH:mm:ss}  {categoryOrLabel} (#{lId})"
            : $"{dateTime:HH:mm:ss}  {ToKorean(categoryOrLabel)} 감지 (#{lId})";
    }

    private static string ToKorean(string category) => category switch
    {
        "human" or "person" => "사람",
        "animal" => "동물",
        "vehicle" => "차량",
        "motion" => "움직임",
        _ => category,
    };
}
