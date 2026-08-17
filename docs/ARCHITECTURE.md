# Личный дневник в Telegram → HTML-отчёт

Архитектурный план сервиса на .NET 10.

---

## 1. Что строим

Ты в течение дня/недели кидаешь в свой Telegram-бот текст и голосовые. Ничего не структурируешь, не нажимаешь кнопок, не выбираешь категорию — просто говоришь. Потом запускаешь анализ, он поднимает локальную LLM в LM Studio и выдаёт один красивый самодостаточный HTML-файл.

Три потока данных в одном канале:

| Поток | Что говоришь | Что нужно на выходе |
|---|---|---|
| **ЖКТ** | «съел борщ и жареную картошку», позже «изжога, часа через два, тройка по десятке» | связка «еда → симптом», подозреваемые продукты, динамика |
| **Идеи** | наговорил мысль на 40 секунд, чтобы не потерять | карточки, сгруппированные по темам, с датой |
| **Вопросы** | «а почему в EF Core split query быстрее?» | чеклист, сгруппированный по темам |

---

## 2. Главное ограничение, которое ломает наивный дизайн

> **Telegram Bot API хранит непрочитанные апдейты только 24 часа.**

`getUpdates` отдаёт то, что накопилось максимум за сутки. Сценарий «включу сервис в конце года, он подключится к Telegram и заберёт всё» с Bot API **невозможен**: за год ты потеряешь 364 дня записей. Плюс `getFile` даёт ссылку на аудио, живущую ограниченное время — файлы надо забирать сразу.

Поэтому система разделена на **два процесса** с разными жизненными циклами:

```
┌─────────────────────────────────────────────────────────────────────┐
│  Diary.Collector  — лёгкий, всегда запущен (Windows Service)        │
│  long-polling → скачать voice → сложить в SQLite + data/voice/      │
│  ~30 МБ RAM, 0% CPU в простое. LLM НЕ трогает.                      │
└───────────────────────────────┬─────────────────────────────────────┘
                                │  diary.db (single source of truth)
┌───────────────────────────────▼─────────────────────────────────────┐
│  Diary.Cli  — запускается руками, когда захотел отчёт                │
│  транскрипция → LLM-разбор → детерминированная аналитика → HTML      │
└─────────────────────────────────────────────────────────────────────┘
```

Приём и разбор разнесены не из любви к микросервисам, а потому что у них разные требования: приём обязан быть непрерывным и дешёвым, разбор — тяжёлый и по требованию.

**Альтернатива, если always-on демон неприемлем.** MTProto-клиент под твоим личным аккаунтом (`WTelegramClient`) читает **всю историю** чата или «Избранного» — тогда действительно можно запускать раз в год. Цена: логин по телефону, файл сессии, и это уже user-account API, а не бот. В архитектуре оба варианта — просто две реализации одного порта `IMessageSource`, выбираются конфигом. Рекомендация: **Collector как основной путь, MTProto — как «пылесос» для восстановления пропущенного**.

---

## 3. Структура решения

```
tgDictionaryBot.sln
├── src/
│   ├── Diary.Domain/                        ← ноль зависимостей, только BCL
│   ├── Diary.Application/                   ← use cases + порты (интерфейсы)
│   ├── Diary.Infrastructure.Persistence/    ← EF Core 10 + SQLite
│   ├── Diary.Infrastructure.Telegram/       ← Telegram.Bot (Bot API)
│   ├── Diary.Infrastructure.TelegramUser/   ← WTelegramClient (MTProto), опционально
│   ├── Diary.Infrastructure.Transcription/  ← Whisper.net + декодер OGG/Opus
│   ├── Diary.Infrastructure.Llm/            ← LM Studio через Microsoft.Extensions.AI
│   ├── Diary.Infrastructure.Reporting/      ← Razor-компоненты → HTML-строка
│   ├── Diary.Collector/                     ← Worker Service, always-on
│   └── Diary.Cli/                           ← System.CommandLine, по требованию
└── tests/
    ├── Diary.Domain.Tests/                  ← математика корреляций
    ├── Diary.Application.Tests/             ← пайплайн на фейковых портах
    ├── Diary.Reporting.Tests/               ← snapshot-тесты HTML
    └── Diary.Llm.ContractTests/             ← на записанных ответах модели, офлайн
```

Правило зависимостей — строго внутрь:

```
Collector ─┐                    ┌─ Persistence ─┐
           ├→ Application ──────┤─ Telegram     ├→ Domain
Cli ───────┘        ↑           │─ Llm          │
                    │           │─ Transcription│
              (только порты)    └─ Reporting ───┘
```

`Domain` не знает про EF, HTTP, Telegram и LLM. `Application` не знает про конкретные технологии — только про свои интерфейсы. Инфраструктура реализует эти интерфейсы и подключается в композиционном корне (`Program.cs` каждого хоста).

> Проекты названы `Diary.*`, репозиторий исторически `tgDictionaryBot` — при желании переименовывается одним find&replace до первой строчки кода.

---

## 4. Доменная модель

### 4.1 Два уровня данных

**`CapturedMessage`** — сырьё. Ровно то, что пришло из Telegram, плюс транскрипт. Никогда не меняется по смыслу, только двигается по состояниям обработки. Это твой архив: даже если LLM-разбор окажется мусорным, исходник цел и переразбор бесплатен.

```csharp
public sealed class CapturedMessage
{
    public MessageId Id { get; }                       // Guid v7 — сортируется по времени
    public long TelegramMessageId { get; }             // UNIQUE → идемпотентность приёма
    public long ChatId { get; }
    public DateTimeOffset SentAtUtc { get; }
    public MessageKind Kind { get; }                   // Text | Voice
    public string? Text { get; }
    public VoiceAsset? Voice { get; }                  // путь к .ogg, длительность
    public long? ReplyToTelegramMessageId { get; }     // явная связка симптом → приём пищи
    public IReadOnlyList<string> Hashtags { get; }     // #еда #симптом #идея #вопрос
    public Transcript? Transcript { get; }
    public ProcessingState State { get; }
    public string? FailureReason { get; }
}

public enum ProcessingState { Captured, Transcribed, Extracted, Failed, Skipped }
```

**`DiaryEntry`** — смысл. Одно сообщение даёт **1..N** записей: «съел борщ, кстати идея — сделать линтер для промптов, и надо загуглить чем span отличается от memory» — это три разные записи из одного голосового.

```csharp
public abstract class DiaryEntry
{
    public EntryId Id { get; }
    public MessageId SourceMessageId { get; }     // всегда можно вернуться к исходнику
    public DateTimeOffset OccurredAtUtc { get; }  // ≠ время отправки: «вчера вечером ел…»
    public bool OccurredAtIsExact { get; }        // разрешённое относительное время помечено
    public string RawFragment { get; }            // кусок транскрипта, породивший запись
    public Confidence Confidence { get; }         // 0..1 от модели
    public abstract EntryCategory Category { get; }
}

public sealed class MealEntry : DiaryEntry        // FoodItem[], MealType
public sealed class SymptomEntry : DiaryEntry     // SymptomKind, Severity, Duration?, LinkedMealId?
public sealed class IdeaEntry : DiaryEntry        // Title, Body, Themes[], IsActionable
public sealed class QuestionEntry : DiaryEntry    // Question, Topic, Answer?
public sealed class UnclassifiedEntry : DiaryEntry
```

### 4.2 Value objects

```csharp
public readonly record struct Severity           // 0..10, конструктор валидирует
public readonly record struct Confidence         // 0..1
public sealed record FoodItem(
    string CanonicalName,                        // "картофель жареный"
    string RawName,                              // "жарёха", как сказал
    Quantity? Quantity,
    IReadOnlySet<FoodTag> Tags);

[Flags] public enum FoodTag                      // Fatty, Fried, Spicy, Acidic, Dairy,
                                                 // Gluten, Caffeine, Alcohol, Carbonated,
                                                 // Legumes, RawVegetables, Sweet, Processed
public enum SymptomKind { Reflux, Heartburn, Bloating, Gas, Diarrhea,
                          Constipation, Nausea, AbdominalPain, Belching, Other }
```

### 4.3 Доменные сервисы — детерминированные, без LLM

Это принципиальный момент. **Модель только извлекает структуру из речи. Всё, что считается — считается кодом.**

```csharp
public interface IFoodSymptomCorrelator
{
    CorrelationReport Analyze(IReadOnlyList<MealEntry> meals,
                             IReadOnlyList<SymptomEntry> symptoms,
                             ExposureWindowPolicy policy);
}

public sealed class SymptomTimelineBuilder      // раскладка по дням/часам для графика
public sealed class FoodCanonicalizer           // алиасы: "жарёха" → "картофель жареный"
public sealed class RelativeTimeResolver        // {-1 день, вечер} + SentAt → абсолютный UTC
public sealed class EntryDeduplicator           // «я же говорил про борщ» — одно и то же
```

Почему так: 8-миллиардная модель ошибается в арифметике и не воспроизводима между запусками. Корреляции обязаны быть тестируемыми, объяснимыми и одинаковыми при повторном прогоне. Плюс — Domain.Tests покрывают самую ценную логику без единого HTTP-вызова.

---

## 5. Application: порты и сценарии

### 5.1 Порты (интерфейсы, которые реализует инфраструктура)

```csharp
// приём
public interface IMessageSource                                   // Bot API | MTProto
{
    IAsyncEnumerable<IncomingMessage> FetchAsync(IngestCheckpoint from, CancellationToken ct);
    Task AcknowledgeAsync(long telegramMessageId, CancellationToken ct);  // ставит реакцию 👀
}
public interface IVoiceStorage                                    // сохранить/прочитать .ogg
public interface IIngestCheckpointStore                           // last update_id

// обработка
public interface IAudioDecoder                                    // OGG/Opus → PCM 16 kHz mono
public interface ITranscriber                                     // PCM → текст (Whisper)
public interface IEntrySegmenter                                  // текст → фрагменты + категории
public interface IEntryExtractor                                  // фрагмент → типизированная запись
{
    EntryCategory Category { get; }
    Task<DiaryEntry> ExtractAsync(EntryFragment fragment, ExtractionContext ctx, CancellationToken ct);
}

// хранение
public interface ICapturedMessageRepository
public interface IDiaryEntryRepository
public interface IUnitOfWork

// вывод
public interface IReportRenderer                                  // ReportViewModel → HTML-строка
public interface IReportWriter                                    // строка → файл, вернуть путь

// системное — TimeProvider (BCL) вместо самописного IClock
```

`IEntryExtractor` — **Strategy**, по одной реализации на категорию, регистрируются через keyed DI (`AddKeyedScoped<IEntryExtractor>(EntryCategory.Meal, …)`). Новая категория («сон», «тренировки», «настроение») добавляется как новый экстрактор + новый промпт + новый partial шаблона отчёта. **Ни один существующий файл не редактируется** — это и есть OCP на практике.

### 5.2 Сценарии

| Use case | Где живёт | Что делает |
|---|---|---|
| `CollectMessagesHandler` | Collector | тянет апдейты, качает голос, пишет `CapturedMessage`, двигает чекпоинт, ставит реакцию |
| `TranscribePendingHandler` | Cli | берёт `State = Captured` + Voice, декод → Whisper → `Transcribed` |
| `ExtractEntriesHandler` | Cli | берёт `Transcribed`, сегментирует, диспатчит в экстракторы, пишет `DiaryEntry`, → `Extracted` |
| `BuildReportHandler` | Cli | грузит записи за период → доменная аналитика → `ReportViewModel` → рендер → файл |
| `AnswerQuestionsHandler` | Cli, опц. | прогоняет накопленные вопросы через LLM, кладёт краткие ответы в отчёт |

---

## 6. Пайплайн: состояния и возобновляемость

```
Telegram ──long polling──► [Captured] ──whisper──► [Transcribed] ──llm──► [Extracted]
                               │                        │                     │
                            data/voice/*.ogg        transcript в БД      DiaryEntry[] в БД
```

Каждый шаг:

- **идемпотентен** — работает только с записями в своём входном состоянии;
- **атомарен** — состояние двигается в той же транзакции, что и результат;
- **возобновляем** — упало на 300-м голосовом из 500 → перезапуск продолжит с 301-го, первые 300 транскрипций не пересчитываются;
- **прослеживаем** — рядом с результатом пишется `ModelId` + `PromptVersion` + `TranscriberModel`.

Последнее даёт бесплатную суперспособность: обновил Qwen — запустил `diary reprocess --from-state Transcribed --since 2026-01-01`, старые извлечения перезаписались новой моделью, а сырьё и транскрипты не тронуты.

Обработка ленивая, через `IAsyncEnumerable` + `System.Threading.Channels`: транскрипция упирается в CPU/GPU и идёт с параллелизмом 1–2, LLM-извлечение — с параллелизмом 2–4 (LM Studio держит очередь). Числа в конфиге.

---

## 7. Слой LLM

**Транспорт.** LM Studio отдаёт OpenAI-совместимый API на `http://localhost:1234/v1`. Берём `Microsoft.Extensions.AI` (`IChatClient`) поверх официального `OpenAI` SDK с подменённым `Endpoint`. Это даёт единый абстрактный клиент, а `Application` про OpenAI вообще не знает — он видит только `IEntrySegmenter` / `IEntryExtractor`.

**Structured output.** Никакого парсинга свободного текста регулярками. `response_format: json_schema` со строгой схемой, `GetResponseAsync<TResult>()` возвращает готовый типизированный объект. Схема генерируется из C#-DTO, так что контракт и код не разъезжаются.

**Два прохода вместо одного.**

1. *Сегментация* — дешёвый вызов: разбить сообщение на смысловые фрагменты и присвоить каждому категорию + уверенность. Отдельно, потому что модель на 8B плохо делает «раздели И извлеки всё сразу» — качество проседает на длинных голосовых.
2. *Извлечение* — свой узкий промпт и своя схема на категорию. Промпт для еды не содержит ни слова про идеи; модели проще, схема меньше, ошибок меньше.

**Особенности Qwen3.** У модели гибридный режим рассуждений — по умолчанию она пишет `<think>…</think>`, что раздувает выдачу и ломает JSON. Отключаем через `chat_template_kwargs: { enable_thinking: false }` (либо `/no_think` в системном промпте), плюс защитный стриппер think-блоков перед десериализацией. `temperature = 0.1`, фиксированный `seed` — ради воспроизводимости.

**Промпты как ресурсы.** `Prompts/segment.v3.ru.md`, `Prompts/extract-meal.v2.ru.md` — embedded resources, версия в имени файла, версия пишется в БД рядом с результатом. Промпт — это код, он живёт в git и ревьюится.

**Устойчивость.** Невалидный JSON → один повторный вызов с «ремонтным» промптом (сообщение об ошибке валидации + требование вернуть только JSON) → если снова мимо, запись уходит в `Failed` с сохранённым сырым ответом. Никаких падений всего прогона из-за одного кривого ответа. Ретраи и таймауты — `Microsoft.Extensions.Http.Resilience`.

**Что LLM делать НЕ разрешено:** считать, агрегировать, ранжировать, делать выводы «этот продукт тебе вреден». Только «текст → структура».

---

## 8. Транскрипция голоса

Telegram отдаёт voice в OGG/Opus, Whisper хочет PCM 16 kHz mono — нужен декодер:

```
data/voice/2026/08/1734.ogg ──IAudioDecoder──► float[] 16kHz ──ITranscriber──► текст
```

- **Транскрайбер:** `Whisper.net` (обёртка над whisper.cpp), модель `ggml-large-v3-turbo` для русского — на GPU идёт быстрее реального времени, на CPU медленнее, но приемлемо для батча раз в неделю. Модель качается один раз в `models/`, в git не лежит.
- **Декодер:** `Concentus` + `NAudio` (чистый .NET, без внешних бинарников) или вызов `ffmpeg`, если он и так стоит. Прячется за `IAudioDecoder`, меняется одной строчкой в DI.
- **Альтернатива за тем же портом:** `faster-whisper` в виде локального HTTP-сайдкара — если захочется скорости и не жалко Python в системе.
- Язык фиксируется `ru` (автодетект на коротких записях промахивается), задаётся initial prompt с типичным словарём — «изжога», «рефлюкс», «вздутие» — заметно поднимает точность на медицинских терминах.
- Транскрипт сохраняется навсегда. Аудио — тоже (десятки КБ на сообщение), чтобы можно было переслушать спорное место.

---

## 9. Аналитика ЖКТ — детерминированная, объяснимая

Самая содержательная часть. Считается в `Domain`, покрывается unit-тестами, не зависит от LLM.

### 9.1 Окна экспозиции

Симптомы имеют разное типичное отставание от приёма пищи, и одно окно на всех даёт мусор. Политика задаётся конфигом:

| Симптом | Окно после приёма |
|---|---|
| Рефлюкс, изжога, отрыжка | 0–4 ч |
| Вздутие, газы | 1–8 ч |
| Диарея, боль | 2–24 ч |
| Запор | 8–48 ч |

### 9.2 Метрики

Для каждой пары (продукт `F`, симптом `S`):

```
a = приёмов F, после которых в окне был S
b = приёмов F, после которых симптома не было
p₁ = a / (a + b)                            — P(S | ел F)
p₀ = базовая частота S по всем приёмам пищи — P(S)
lift = p₁ / p₀
```

Плюс односторонний **точный тест Фишера** → p-value, медианная задержка «приём → симптом», средняя тяжесть. В подозреваемые попадает пара при `support ≥ 3` и `lift ≥ 1.5`; всё остальное показывается как «данных мало» — но показывается, чтобы было видно, чего именно не хватает.

Зеркально считается список **«чистых»** продуктов: `support ≥ 5`, `lift ≤ 0.7` — то, что ты ешь регулярно и без последствий. На практике этот список полезнее списка подозреваемых, потому что даёт что есть, а не только чего избегать.

### 9.3 Корреляции по тегам — то, ради чего вообще заведены `FoodTag`

Конкретный «борщ» за месяц встретится 3 раза — статистики нет. А тег `Fatty` — 40 раз, `Spicy` — 25, `Caffeine` — 60. Поэтому **корреляции считаются на двух уровнях одновременно**: по конкретным продуктам и по тегам. Второй уровень начинает давать сигнал уже на второй неделе, первый — через пару месяцев.

### 9.4 Честность

В отчёте рядом с каждой цифрой — `n`, и явный блок про множественные сравнения: чем больше продуктов проверяется, тем выше шанс случайного «открытия». Плюс дисклеймер, что это дневник наблюдений, а не диагностика.

### 9.5 Жёсткие связки вместо догадок

Если симптом описан **ответом (reply)** на сообщение о еде — связь фиксируется напрямую, без всякой эвристики окон. Это самый точный сигнал в системе, и он стоит одного свайпа в Telegram. Такие связки помечаются в отчёте как подтверждённые.

---

## 10. Отчёт

**Рендер:** Razor-компоненты + `HtmlRenderer` из `Microsoft.AspNetCore.Components.Web` — статический серверный рендер прямо из консольного приложения. Плюсы против шаблонизатора-строки: типобезопасность, компилятор ловит опечатки в модели, компоненты переиспользуются, snapshot-тесты тривиальны. (Лёгкая альтернатива — `Scriban`, если не хочется тянуть `FrameworkReference` на ASP.NET Core.)

**Формат:** **один самодостаточный `.html`**. Инлайн CSS, инлайн SVG, ноль внешних запросов, ноль CDN. Файл открывается офлайн, кладётся в архив, живёт десять лет, читается с флешки. Никакого Chart.js — графики рисуются как SVG прямо из view-модели.

**Структура:**

1. **Шапка** — период, сколько сообщений/голосовых/минут наговорено.
2. **Дашборд ЖКТ** — тепловая карта дней по тяжести, таймлайн «еда × симптомы» по часам, таблица подозреваемых с барами уверенности, топ симптомов, список «чистых» продуктов.
3. **Идеи** — карточки, сгруппированные по темам, с бейджем «actionable» и датой.
4. **Вопросы** — чеклист по темам, опционально с краткими ответами модели.
5. **Приложение** — сырой лог в `<details>`: транскрипты, время, ссылки на исходные `.ogg`.

Тёмная/светлая тема через `prefers-color-scheme`, отдельные `@media print` стили. Живой пример с выдуманными данными — [`docs/report-sample.html`](report-sample.html).

**ViewModel** строится в `Application` и **не содержит доменных сущностей** — только готовые к отображению примитивы и предпосчитанные координаты SVG. Шаблон ничего не вычисляет: вся логика тестируется без HTML.

---

## 11. Как этим пользоваться

**Сбор — ничего не надо делать.** Пишешь и наговариваешь как в заметки. Бот ставит реакцию 👀 — значит, поймал.

Три необязательных приёма, которые заметно поднимают качество:

- **`reply` на сообщение о еде**, когда описываешь симптом → жёсткая связка вместо угадывания;
- **хэштеги** `#еда #симптом #идея #вопрос` → детерминированная категория в обход LLM (и рабочий fallback, если модель не завелась);
- **называть тяжесть числом** — «изжога на четвёрку» → `Severity = 4`, иначе модель ставит по прилагательным и шкала плывёт.

**Разбор:**

```bash
dotnet run --project src/Diary.Cli -- report --period week --open
dotnet run --project src/Diary.Cli -- report --from 2026-01-01 --to 2026-12-31
dotnet run --project src/Diary.Cli -- transcribe          # только транскрипция
dotnet run --project src/Diary.Cli -- reprocess --from-state Transcribed --since 2026-06-01
dotnet run --project src/Diary.Cli -- status              # что накопилось и в каком состоянии
```

CLI на `System.CommandLine`. `--open` открывает готовый HTML в браузере.

---

## 12. Конфигурация

`appsettings.json` в репозитории — без секретов. Токен только через `dotnet user-secrets` или переменную окружения `DIARY_Telegram__BotToken`.

```jsonc
{
  "Telegram": { "Source": "BotApi", "AllowedUserId": 123456789 },
  "LmStudio": {
    "Endpoint": "http://localhost:1234/v1",
    "Model": "qwen3-8b",
    "Temperature": 0.1,
    "Seed": 42,
    "EnableThinking": false,
    "MaxParallelRequests": 3
  },
  "Whisper": { "ModelPath": "models/ggml-large-v3-turbo.bin", "Language": "ru" },
  "Analysis": {
    "TimeZone": "Europe/Moscow",
    "MinSupport": 3,
    "MinLift": 1.5,
    "Windows": { "Reflux": "0:00-4:00", "Bloating": "1:00-8:00", "Diarrhea": "2:00-24:00" }
  },
  "Report": { "OutputDirectory": "reports", "Locale": "ru-RU" }
}
```

Всё через `IOptions<T>` с `ValidateDataAnnotations().ValidateOnStart()` — кривой конфиг роняет процесс на старте с внятным текстом, а не через сорок минут транскрипции.

`AllowedUserId` — жёсткий фильтр: бот принимает сообщения только от тебя. Токен бота рано или поздно утечёт или его найдут перебором, а дневник — это медицинские данные.

---

## 13. Где здесь SOLID

| Принцип | Как проявляется |
|---|---|
| **SRP** | `Collector` только принимает, `Transcriber` только распознаёт, `Extractor` только извлекает, `Correlator` только считает. Ни один класс не знает про два этапа сразу. |
| **OCP** | Новая категория записей = новый `IEntryExtractor` + промпт + partial шаблона + строка регистрации в DI. Существующий код не редактируется. |
| **LSP** | Все `IEntryExtractor` взаимозаменяемы; `IMessageSource` подменяется с Bot API на MTProto без изменений в `Application`. |
| **ISP** | Порты узкие: `IVoiceStorage` не умеет читать текст, `ITranscriber` не умеет качать файлы. Никаких `IDiaryService` на двадцать методов. |
| **DIP** | `Application` зависит только от собственных интерфейсов; реализации подставляются в композиционном корне хоста. |

Дополнительно: **отделение «извлечения» от «вывода»** — недетерминированная часть (LLM) изолирована в узкой прослойке, всё остальное чисто и тестируемо. Это ценнее любой буквы из аббревиатуры.

---

## 14. Тестирование

| Слой | Как |
|---|---|
| `Domain` | обычные unit-тесты: окна экспозиции, lift, Фишер, резолв относительного времени, дедуп. Самая ценная логика — без единого мока. |
| `Application` | фейковые порты в памяти; проверяем идемпотентность, возобновление после падения, корректность переходов состояний. |
| `Reporting` | snapshot-тесты (`Verify`): ViewModel → HTML, дифф на изменения. |
| `Llm` | контрактные тесты на **записанных** ответах модели (фикстуры в git): проверяем парсинг, стриппер `<think>`, ремонтный ретрай. Офлайн, в CI. |
| Живая LLM | отдельная категория тестов `[Trait("Category","RequiresLmStudio")]`, из CI исключена. |

---

## 15. Приватность

Дневник еды и симптомов — медицинские данные, и они не должны покидать машину. Здесь это не лозунг, а следствие архитектуры: LLM локальная, Whisper локальный, БД локальная, отчёт локальный. Наружу уходит ровно один HTTPS-канал — long polling к api.telegram.org, который и так неизбежен.

- `data/`, `reports/`, `models/`, `*.db`, `*.ogg` — в `.gitignore`, репозиторий публичный.
- Токен — в user-secrets/переменной окружения, не в файлах.
- `AllowedUserId` отсекает чужие сообщения.
- Опционально: SQLCipher, если ноутбук ездит с тобой.

---

## 16. Порядок работ

| Этап | Результат | Ценность |
|---|---|---|
| **M0** | Collector: приём текста и голоса, SQLite, реакция 👀, `AllowedUserId` | **записи перестают теряться** — самое срочное |
| **M1** | Whisper + декодер OGG, `diary transcribe` | голосовые стали текстом |
| **M2** | Сегментация + 4 экстрактора, БД записей | текст стал структурой |
| **M3** | HTML-отчёт: идеи, вопросы, лента еды/симптомов | первый читаемый выхлоп |
| **M4** | Корреляции: окна, lift, Фишер, теги, «чистые» продукты | ради чего всё затевалось |
| **M5** | `reprocess`, ответы на вопросы, MTProto-адаптер, экспорт в Markdown/JSON | зрелость |

M0 стоит сделать в первую очередь и запустить, даже если остального ещё нет: он копит сырьё, а разбор к накопленному применяется задним числом в любой момент.

---

## 17. Зависимости

Версии централизованы в `Directory.Packages.props`.

| Назначение | Пакет |
|---|---|
| Telegram Bot API | `Telegram.Bot` |
| Telegram MTProto (опц.) | `WTelegramClient` |
| Хост, DI, конфиг, логи | `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http.Resilience` |
| Windows Service | `Microsoft.Extensions.Hosting.WindowsServices` |
| LLM | `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OpenAI` |
| Транскрипция | `Whisper.net`, `Whisper.net.Runtime` (+ `.Cuda`/`.Vulkan`) |
| Декод OGG/Opus | `Concentus`, `NAudio` |
| БД | `Microsoft.EntityFrameworkCore.Sqlite` |
| CLI | `System.CommandLine` |
| Отчёт | `FrameworkReference: Microsoft.AspNetCore.App` (Razor + `HtmlRenderer`) |
| Логи | `Serilog.Extensions.Hosting`, `Serilog.Sinks.File` |
| Тесты | `xunit.v3`, `Shouldly`, `NSubstitute`, `Verify.Xunit` |
