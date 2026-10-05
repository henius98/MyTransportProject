namespace MyTransportAppWASM.Utils;

public static class MalaysiaTime
{
  public const string IanaTimeZone = "Asia/Kuala_Lumpur";
  public const string DisplayName = "Malaysia Time (UTC+08:00)";

  public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

  public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);

  public static DateTime Today => Now.Date;

  public static DateTimeOffset At(DateTime value) =>
    new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), Offset);

  public static DateTimeOffset Convert(DateTimeOffset value) => value.ToOffset(Offset);

  public static DateTime LocalDateTime(DateTimeOffset value) => Convert(value).DateTime;
}
