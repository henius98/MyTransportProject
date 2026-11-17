using System.Globalization;

namespace MyTransportAppWASM.Features.Weather.Services;

internal interface IWeatherProviderAdapter
{
  string Name { get; }
  WeatherProviderOptions Options { get; }
  int OutlookMaxDays { get; }
  bool IsEligible(WeatherQuery query);
  Uri BuildEndpoint(WeatherQuery query, bool isOutlook);
}

internal static class WeatherProviderTemplates
{
  public static string ApplyCommonPlaceholders(
    string template,
    WeatherProviderOptions options,
    WeatherQuery? query = null)
  {
    if (query is not null)
    {
      template = template
        .Replace("{lat}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lon}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
      template = template
        .Replace("{key}", options.ApiKey, StringComparison.OrdinalIgnoreCase)
        .Replace("{apiKey}", options.ApiKey, StringComparison.OrdinalIgnoreCase);
    }

    return template;
  }
}

internal sealed class MetMalaysiaWeatherAdapter(MetMalaysiaWeatherProviderOptions options) : IWeatherProviderAdapter
{
  public string Name => string.IsNullOrWhiteSpace(options.Name) ? "MetMalaysia" : options.Name;
  public WeatherProviderOptions Options => options;
  public int OutlookMaxDays => 14;

  public bool IsEligible(WeatherQuery query) =>
    options.Enabled && !string.IsNullOrWhiteSpace(query.LocationId);

  public Uri BuildEndpoint(WeatherQuery query, bool isOutlook)
  {
    var template = options.BaseUrl + options.Endpoints;

    if (template.Contains("{location_id}", StringComparison.OrdinalIgnoreCase))
    {
      var locId = !string.IsNullOrWhiteSpace(query.LocationId) ? query.LocationId : "Ds058";
      template = template.Replace("{location_id}", Uri.EscapeDataString(locId), StringComparison.OrdinalIgnoreCase);
    }

    if (template.Contains("{date_start}", StringComparison.OrdinalIgnoreCase) ||
        template.Contains("{date_end}", StringComparison.OrdinalIgnoreCase))
    {
      var currentMalaysiaTime = MalaysiaTime.Now;
      var start = isOutlook ? query.Departure ?? currentMalaysiaTime : currentMalaysiaTime;
      var end = isOutlook ? query.Return ?? start.AddDays(7) : currentMalaysiaTime;
      template = template.Replace("{date_start}", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
      template = template.Replace("{date_end}", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    return new Uri(WeatherProviderTemplates.ApplyCommonPlaceholders(template, options), UriKind.Absolute);
  }
}

internal abstract class KeyedWeatherProviderAdapter(KeyedWeatherProviderOptions options, string defaultName)
  : IWeatherProviderAdapter
{
  protected KeyedWeatherProviderOptions KeyedOptions { get; } = options;
  public string Name => string.IsNullOrWhiteSpace(KeyedOptions.Name) ? defaultName : KeyedOptions.Name;
  public WeatherProviderOptions Options => KeyedOptions;
  public abstract int OutlookMaxDays { get; }
  public virtual bool IsEligible(WeatherQuery query) => KeyedOptions.Enabled;

  protected static string ModeKey(bool isOutlook) => isOutlook ? "Outlook" : "Realtime";

  protected string GetEndpointTemplate(string endpointKey) =>
    KeyedOptions.BaseUrl + (KeyedOptions.Endpoints.TryGetValue(endpointKey, out var path) ? path : string.Empty);

  public abstract Uri BuildEndpoint(WeatherQuery query, bool isOutlook);
}

internal sealed class OpenMeteoWeatherAdapter(KeyedWeatherProviderOptions options)
  : KeyedWeatherProviderAdapter(options, "Open-Meteo")
{
  public override int OutlookMaxDays => WeatherForecastLimits.OpenMeteoForecastDays;

  public override Uri BuildEndpoint(WeatherQuery query, bool isOutlook) =>
    BuildOpenMeteoUri(GetEndpointTemplate(ModeKey(isOutlook)), query);

  public bool TryBuildHourlyEndpoint(WeatherQuery query, DateTime date, out Uri? uri)
  {
    if (!KeyedOptions.Endpoints.TryGetValue("Hourly", out var hourlyPath))
    {
      uri = null;
      return false;
    }

    var template = (KeyedOptions.BaseUrl + hourlyPath)
      .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
      .Replace("{timezone}", Uri.EscapeDataString(MalaysiaTime.IanaTimeZone), StringComparison.OrdinalIgnoreCase);
    uri = BuildOpenMeteoUri(template, query);
    return true;
  }

  private Uri BuildOpenMeteoUri(string template, WeatherQuery query)
  {
    var formatted = WeatherProviderTemplates.ApplyCommonPlaceholders(template, KeyedOptions, query);

    if (formatted.Contains("{forecast_hours}", StringComparison.OrdinalIgnoreCase))
    {
      formatted = formatted.Replace("{forecast_hours}", "1", StringComparison.OrdinalIgnoreCase);
    }

    if (formatted.Contains("{forecast_days}", StringComparison.OrdinalIgnoreCase))
    {
      var days = 7;
      if (query.Return.HasValue)
      {
        var timeSpan = MalaysiaTime.Convert(query.Return.Value).Date - MalaysiaTime.Today;
        days = Math.Max(1, Math.Min(WeatherForecastLimits.OpenMeteoForecastDays, timeSpan.Days + 2));
      }
      formatted = formatted.Replace("{forecast_days}", days.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    return new Uri(formatted, UriKind.Absolute);
  }
}

internal sealed class SingaporeNeaWeatherAdapter(KeyedWeatherProviderOptions options)
  : KeyedWeatherProviderAdapter(options, "Singapore NEA")
{
  public override int OutlookMaxDays => 4;

  public override bool IsEligible(WeatherQuery query) =>
    base.IsEligible(query) && IsNearSingapore(query.Latitude, query.Longitude);

  public override Uri BuildEndpoint(WeatherQuery query, bool isOutlook)
  {
    var formatted = WeatherProviderTemplates.ApplyCommonPlaceholders(
      GetEndpointTemplate(ModeKey(isOutlook)), KeyedOptions, query);
    return new Uri(formatted, UriKind.Absolute);
  }

  public static bool IsNearSingapore(double lat, double lon) =>
    lat >= 1.15 && lat <= 1.50 && lon >= 103.55 && lon <= 104.15;
}
