# Сборка и прогон дневника в контейнере.
#
# Три цели: build (компиляция), test (прогон тестов), runtime (то, что запускается).
# Тесты живут в образе не ради красоты: в них разбор OGG и математика статистики,
# и прогонять их надо там же, где собирается то, что поедет в работу.

# ─── компиляция ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только манифесты: слой с restore переживает правку кода.
# .editorconfig обязателен — в нём настройки анализаторов, без него сборка
# падает на правилах, которые в проекте осознанно отключены.
COPY global.json Directory.Build.props Directory.Packages.props tgDictionaryBot.slnx .editorconfig ./
COPY src/Diary.Domain/*.csproj                      src/Diary.Domain/
COPY src/Diary.Application/*.csproj                 src/Diary.Application/
COPY src/Diary.Modules.Gi/*.csproj                  src/Diary.Modules.Gi/
COPY src/Diary.Modules.Notes/*.csproj               src/Diary.Modules.Notes/
COPY src/Diary.Infrastructure.Persistence/*.csproj  src/Diary.Infrastructure.Persistence/
COPY src/Diary.Infrastructure.Telegram/*.csproj     src/Diary.Infrastructure.Telegram/
COPY src/Diary.Infrastructure.Speech/*.csproj       src/Diary.Infrastructure.Speech/
COPY src/Diary.Infrastructure.Llm/*.csproj          src/Diary.Infrastructure.Llm/
COPY src/Diary.Infrastructure.Reporting/*.csproj    src/Diary.Infrastructure.Reporting/
COPY src/Diary.Cli/*.csproj                         src/Diary.Cli/
COPY tests/Diary.Domain.Tests/*.csproj              tests/Diary.Domain.Tests/
COPY tests/Diary.Application.Tests/*.csproj         tests/Diary.Application.Tests/
COPY tests/Diary.Modules.Gi.Tests/*.csproj          tests/Diary.Modules.Gi.Tests/
COPY tests/Diary.Reporting.Tests/*.csproj           tests/Diary.Reporting.Tests/
COPY tests/Diary.Speech.Tests/*.csproj              tests/Diary.Speech.Tests/
COPY tests/Diary.Llm.Tests/*.csproj                 tests/Diary.Llm.Tests/
RUN dotnet restore

COPY src/ src/
COPY tests/ tests/
RUN dotnet build -c Release --no-restore

RUN dotnet publish src/Diary.Cli -c Release -o /app --no-build

# ─── тесты ─────────────────────────────────────────────────────────────────
# xunit.v3 собирает тестовые проекты как исполняемые файлы, поэтому запускаем
# их напрямую: dotnet test на .NET 10 SDK требует отдельного включения раннера.
FROM build AS test
RUN for project in tests/*/; do \
        echo "── $(basename "$project")" && \
        dotnet run --project "$project" -c Release --no-build || exit 1; \
    done

# ─── работа ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# libicu — отчёт форматирует даты и числа в ru-RU, инвариантная глобализация не годится.
# libgomp1 — нативная часть whisper.cpp.
# tzdata — время хранится в UTC, а показывается в таймзоне субъекта.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu-dev libgomp1 tzdata ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Каталоги существуют даже без смонтированных томов — иначе первый запуск падает.
RUN mkdir -p /app/data /app/reports /app/models

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DIARY_DataDirectory=/app/data \
    DIARY_ReportDirectory=/app/reports \
    DIARY_Speech__ModelPath=/app/models/ggml-large-v3-turbo.bin

ENTRYPOINT ["dotnet", "/app/diary.dll"]
CMD ["--help"]
