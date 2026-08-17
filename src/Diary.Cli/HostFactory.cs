using Diary.Application;
using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Reporting;
using Diary.Application.Speech;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Diary.Cli.Configuration;
using Diary.Infrastructure.Llm;
using Diary.Infrastructure.Persistence;
using Diary.Infrastructure.Reporting;
using Diary.Infrastructure.Speech;
using Diary.Infrastructure.Telegram;
using Diary.Modules.Gi;
using Diary.Modules.Notes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diary.Cli;

/// <summary>
/// Композиционный корень. Единственное место, где ядро встречается с конкретными
/// реализациями портов.
/// </summary>
public static class HostFactory
{
    public static IHost Build(string? sourceSpec, bool verbose, string? modelOverride = null)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables("DIARY_");

        // Сравнение моделей не должно требовать правки конфига между прогонами.
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LlmOptions.SectionName}:Roles:Segmentation:Model"] = modelOverride,
                [$"{LlmOptions.SectionName}:Roles:Extraction:Model"] = modelOverride,
                [$"{LlmOptions.SectionName}:Roles:Answering:Model"] = modelOverride,
            });
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        var config = ConfigurationMapper.Read(builder.Configuration);
        var subjects = ConfigurationMapper.BuildSubjects(config);

        var services = builder.Services;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(config);

        services.AddDiaryApplication(subjects);
        services.AddDiaryModule<GiModule>();
        services.AddDiaryModule<NotesModule>();

        services.AddDiaryPersistence(config.DataDirectory);

        services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
        services.Configure<SpeechOptions>(builder.Configuration.GetSection(SpeechOptions.SectionName));
        services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));

        services.AddSingleton<LmStudioChatClient>();
        services.AddSingleton<IStructuredCompletion, LmStudioCompletion>();
        services.AddSingleton<IToolCallingCompletion, LmStudioToolCalling>();
        services.AddSingleton<IEntrySegmenter, LlmEntrySegmenter>();

        services.AddSingleton<IAudioDecoder, OggOpusDecoder>();
        services.AddScoped<IVoiceStorage, FileSystemVoiceStorage>();
        RegisterUtteranceReader(services, builder.Configuration);

        services.AddSingleton<IReportRenderer, HtmlReportRenderer>();

        RegisterMessageSource(services, sourceSpec);

        // SyncHandler живёт вне скоупа субъекта: он и решает, чей это субъект.
        services.AddSingleton(provider => new SyncHandler(
            provider.GetRequiredService<IMessageSource>(),
            provider.GetRequiredService<ISubjectScopeFactory>(),
            new ScopedCursorStore(provider),
            new ScopedQuarantineStore(provider),
            ConfigurationMapper.ParseForwardPolicy(config.ForwardPolicy),
            builder.Configuration.GetValue<bool>($"{TelegramOptions.SectionName}:MarkAsRead"),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SyncHandler>>()));

        return builder.Build();
    }

    /// <summary>
    /// Три пути к тексту за одним портом. Каскад через Whisper — по умолчанию: транскрипт
    /// нужен в любом случае, а специализированный распознаватель точнее универсальной
    /// модели своего размера.
    /// </summary>
    private static void RegisterUtteranceReader(IServiceCollection services, IConfiguration configuration)
    {
        var kind = configuration.GetValue($"{SpeechOptions.SectionName}:Reader", SpeechReaderKind.Whisper);

        switch (kind)
        {
            case SpeechReaderKind.NativeAudio:
                services.AddSingleton<IUtteranceReader, NativeAudioUtteranceReader>();
                break;

            case SpeechReaderKind.Hybrid:
                services.AddSingleton<WhisperUtteranceReader>();
                services.AddSingleton<NativeAudioUtteranceReader>();
                services.AddSingleton<IUtteranceReader>(provider => new HybridUtteranceReader(
                    provider.GetRequiredService<WhisperUtteranceReader>(),
                    provider.GetRequiredService<NativeAudioUtteranceReader>(),
                    provider.GetRequiredService<IOptions<SpeechOptions>>(),
                    provider.GetRequiredService<ILogger<HybridUtteranceReader>>()));
                break;

            case SpeechReaderKind.Whisper:
            default:
                services.AddSingleton<IUtteranceReader, WhisperUtteranceReader>();
                break;
        }
    }

    private static void RegisterMessageSource(IServiceCollection services, string? sourceSpec)
    {
        // «file:путь» — отладочный источник: весь пайплайн прогоняется без сети и аккаунта.
        if (sourceSpec is { Length: > 0 } spec &&
            spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = spec["file:".Length..];
            services.AddSingleton<IMessageSource>(provider =>
                new FileMessageSource(path, provider.GetRequiredService<ILogger<FileMessageSource>>()));
            return;
        }

        services.AddSingleton<IMessageSource, TelegramMessageSource>();
    }

    /// <summary>
    /// Курсоры и карантин живут в общей базе, но их контекст — scoped. Обёртка открывает
    /// скоуп на операцию, чтобы синхронизация могла пользоваться ими вне скоупа субъекта.
    /// </summary>
    private sealed class ScopedCursorStore(IServiceProvider provider) : ISyncCursorStore
    {
        public async Task<SyncCursor?> GetAsync(long peerId, CancellationToken ct)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISyncCursorStore>().GetAsync(peerId, ct);
        }

        public async Task SaveAsync(SyncCursor cursor, CancellationToken ct)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISyncCursorStore>().SaveAsync(cursor, ct);
        }
    }

    private sealed class ScopedQuarantineStore(IServiceProvider provider) : IQuarantineStore
    {
        public async Task AddAsync(QuarantinedMessage message, CancellationToken ct)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IQuarantineStore>().AddAsync(message, ct);
        }

        public async Task<IReadOnlyList<QuarantinedMessage>> GetAllAsync(CancellationToken ct)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IQuarantineStore>().GetAllAsync(ct);
        }

        public async Task<int> CountAsync(CancellationToken ct)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IQuarantineStore>().CountAsync(ct);
        }
    }
}
