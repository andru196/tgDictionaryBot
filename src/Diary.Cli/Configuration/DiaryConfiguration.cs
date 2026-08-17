using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Configuration;

namespace Diary.Cli.Configuration;

/// <summary>Секция субъекта в appsettings.json, до превращения в доменное описание.</summary>
public sealed class SubjectConfig
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string TimeZone { get; set; } = "Europe/Moscow";

    public string DataDirectory { get; set; } = string.Empty;

    public List<string> Modules { get; } = [];

    public List<SourceConfig> Sources { get; } = [];

    public RetentionConfig? Retention { get; set; }

    public AnalysisConfig? Analysis { get; set; }
}

public sealed class SourceConfig
{
    public string Peer { get; set; } = string.Empty;

    public List<long> SenderIds { get; } = [];

    public bool Exclusive { get; set; }
}

public sealed class RetentionConfig
{
    public string? Mode { get; set; }

    public string? RequiresState { get; set; }

    public TimeSpan? MinAge { get; set; }

    public bool? KeepFailed { get; set; }

    public bool? DryRun { get; set; }
}

public sealed class AnalysisConfig
{
    public int? MinSupport { get; set; }

    public double? MinLift { get; set; }

    public int? ToleratedMinSupport { get; set; }

    public double? ToleratedMaxLift { get; set; }

    public bool? UseCalibratedWindows { get; set; }

    public int? CalibrationMinSamples { get; set; }
}

public sealed class DiaryConfig
{
    public List<SubjectConfig> Subjects { get; } = [];

    public RetentionConfig? Retention { get; set; }

    public AnalysisConfig? Analysis { get; set; }

    public string DataDirectory { get; set; } = "data";

    public string ReportDirectory { get; set; } = "reports";

    public string ForwardPolicy { get; set; } = "Skip";
}

public static class ConfigurationMapper
{
    /// <summary>
    /// Переводит конфигурацию в доменные описания, накладывая общие значения как основу
    /// и настройки субъекта поверх.
    /// </summary>
    public static IReadOnlyList<SubjectDefinition> BuildSubjects(DiaryConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var defaultRetention = ApplyRetention(new RetentionSettings(), config.Retention);
        var defaultAnalysis = ApplyAnalysis(new AnalysisSettings(), config.Analysis);

        var subjects = new List<SubjectDefinition>(config.Subjects.Count);

        foreach (var subject in config.Subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.Key))
            {
                throw new InvalidOperationException("У субъекта в конфигурации не задан Key.");
            }

            var dataDirectory = string.IsNullOrWhiteSpace(subject.DataDirectory)
                ? Path.Combine(config.DataDirectory, subject.Key)
                : subject.DataDirectory;

            subjects.Add(new SubjectDefinition
            {
                Key = new SubjectKey(subject.Key),
                DisplayName = string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.Key : subject.DisplayName,
                TimeZone = ResolveTimeZone(subject.TimeZone),
                DataDirectory = dataDirectory,
                Modules = subject.Modules.Count > 0 ? subject.Modules : ["gi", "notes"],
                Sources = [.. subject.Sources.Select(s =>
                    new SubjectSource(s.Peer, s.SenderIds, s.Exclusive))],
                Retention = ApplyRetention(defaultRetention, subject.Retention),
                Analysis = ApplyAnalysis(defaultAnalysis, subject.Analysis),
            });
        }

        return subjects;
    }

    public static ForwardPolicy ParseForwardPolicy(string value) =>
        Enum.TryParse<ForwardPolicy>(value, ignoreCase: true, out var parsed) ? parsed : ForwardPolicy.Skip;

    /// <summary>
    /// IANA-имена работают и на Windows начиная с .NET 6, но локальные имена тоже надо принять:
    /// в конфиге легко оказаться и «Europe/Moscow», и «Russian Standard Time».
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) &&
                windowsId is not null)
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }

            throw new InvalidOperationException(
                $"Часовой пояс «{id}» не найден. Укажи IANA-идентификатор, например Europe/Moscow.");
        }
    }

    private static RetentionSettings ApplyRetention(RetentionSettings baseline, RetentionConfig? overrides)
    {
        if (overrides is null)
        {
            return baseline;
        }

        return baseline with
        {
            Mode = Enum.TryParse<RetentionMode>(overrides.Mode, ignoreCase: true, out var mode)
                ? mode
                : baseline.Mode,
            RequiresState = Enum.TryParse<ProcessingState>(overrides.RequiresState, ignoreCase: true, out var state)
                ? state
                : baseline.RequiresState,
            MinAge = overrides.MinAge ?? baseline.MinAge,
            KeepFailed = overrides.KeepFailed ?? baseline.KeepFailed,
            DryRun = overrides.DryRun ?? baseline.DryRun,
        };
    }

    private static AnalysisSettings ApplyAnalysis(AnalysisSettings baseline, AnalysisConfig? overrides)
    {
        if (overrides is null)
        {
            return baseline;
        }

        return baseline with
        {
            MinSupport = overrides.MinSupport ?? baseline.MinSupport,
            MinLift = overrides.MinLift ?? baseline.MinLift,
            ToleratedMinSupport = overrides.ToleratedMinSupport ?? baseline.ToleratedMinSupport,
            ToleratedMaxLift = overrides.ToleratedMaxLift ?? baseline.ToleratedMaxLift,
            UseCalibratedWindows = overrides.UseCalibratedWindows ?? baseline.UseCalibratedWindows,
            CalibrationMinSamples = overrides.CalibrationMinSamples ?? baseline.CalibrationMinSamples,
        };
    }

    public static DiaryConfig Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var config = new DiaryConfig();
        configuration.Bind(config);
        return config;
    }
}
