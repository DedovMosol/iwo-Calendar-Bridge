using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using IwoBridge.Core.Reading;
using IwoBridge.Core.Time;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Демонстрационный режим (<c>IwoCalendarBridge.exe --demo</c>): весь экран очистки работает на выдуманном
    /// календаре в памяти, без сервера и без сети. Нужен, чтобы посмотреть и проверить интерфейс — в том числе
    /// предпросмотр тысяч строк, ход удаления и остановку — до подключения к настоящему ящику.
    ///
    /// Данные синтетические: «затопленный» период из тысяч одинаковых копий, обычные события, серии (внутри
    /// периода, на его границе, с перенесённым исключением), заметка. Каждый запуск начинается с тех же данных.
    /// Манифесты пишутся во временную папку пользователя, не рядом с настоящими.
    /// </summary>
    public sealed class DemoSession : ITargetSession
    {
        /// <summary>Подтверждающая фраза демонстрационного ящика.</summary>
        public const string DemoMailbox = "demo@example.test";

        /// <summary>
        /// Пояс выдуманного календаря — постоянный, не пояс этого компьютера: снимки и рассказы о демонстрационном
        /// режиме не должны выдавать, где живёт пользователь.
        /// </summary>
        public static readonly TimeSpan DemoOffset = TimeSpan.FromHours(3);

        // Небольшие задержки показывают ход работы и остановку так, как они выглядят на настоящем сервере.
        private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(60);

        public DemoSession(DateTime today, TimeSpan utcOffset)
        {
            TimeZone = TargetTimeZone.FromOffset(utcOffset);
            InMemoryCalendar calendar = new InMemoryCalendar(Seed(today.Date, TimeZone));
            var slow = new SlowCalendar(calendar);
            string manifests = Path.Combine(Path.GetTempPath(), "iwo Calendar Bridge demo manifests");
            Runner = new CleanupRunner(slow, calendar, calendar, slow, new CsvCleanupManifestWriter(manifests, TimeZone));
        }

        public string TargetLabel => "Демонстрационный ящик " + DemoMailbox;

        public string ConfirmationPhrase => DemoMailbox;

        public TargetTimeZone TimeZone { get; }

        public CleanupRunner Runner { get; }

        public bool IsDemo => true;

        public UserMessage Describe(Exception exception)
        {
            return ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown, exception?.Message);
        }

        /// <summary>Сети и соединений нет — освобождать нечего.</summary>
        public void Dispose()
        {
        }

        /// <summary>Выдуманный календарь относительно сегодняшней даты.</summary>
        private static IEnumerable<InMemoryEvent> Seed(DateTime today, TargetTimeZone zone)
        {
            DateTime Utc(DateTime local, int hour) => DateTime.SpecifyKind(local.AddHours(hour) - zone.Offset, DateTimeKind.Utc);
            DateTime monthAgo = today.AddMonths(-1);

            // «Затопление» прежним инструментом: 2 400 копий за последний месяц.
            for (int i = 0; i < 2400; i++)
            {
                DateTime day = monthAgo.AddDays(i % 30);
                int hour = 8 + (i % 10);
                yield return InMemoryEvent.Single(
                    "flood-" + i, Utc(day, hour), Utc(day, hour + 1), subject: "Копия: Планёрка отдела №" + (i % 40 + 1));
            }

            // Обычные события до и после «затопленного» месяца.
            for (int i = 0; i < 120; i++)
            {
                DateTime day = today.AddDays(-200 + i * 3);
                yield return InMemoryEvent.Single("own-" + i, Utc(day, 10), Utc(day, 11), subject: "Встреча " + (i + 1));
            }

            // Серия целиком внутри месяца.
            var inside = new List<InMemoryOccurrence>();
            for (int i = 0; i < 4; i++)
            {
                DateTime day = monthAgo.AddDays(3 + i * 7);
                inside.Add(new InMemoryOccurrence("weekly-" + i, Utc(day, 9), Utc(day, 10)));
            }

            yield return InMemoryEvent.Master("weekly", "DEMO-WEEKLY", inside, subject: "Еженедельная сводка (копия)");

            // Ежедневная серия через границу месяца с исключением, перенесённым внутрь.
            var boundary = new List<InMemoryOccurrence>();
            for (int i = 0; i < 60; i++)
            {
                DateTime day = monthAgo.AddDays(-30 + i);
                bool moved = i == 40;
                boundary.Add(new InMemoryOccurrence(
                    "daily-" + i, Utc(day, moved ? 15 : 9), Utc(day, moved ? 16 : 10), moved, moved ? "Сводка (перенесена)" : null));
            }

            yield return InMemoryEvent.Master("daily", "DEMO-DAILY", boundary, subject: "Ежедневная сводка");

            yield return InMemoryEvent.Single("note", Utc(today, 12), Utc(today, 12), itemClass: "IPM.Note", subject: "Заметка в календаре");
        }

        /// <summary>Задержка чтения страниц и удаления пакетов — только для наглядности демонстрации.</summary>
        private sealed class SlowCalendar : ITargetPageReader, ITargetDeleter
        {
            private readonly InMemoryCalendar _inner;

            public SlowCalendar(InMemoryCalendar inner)
            {
                _inner = inner;
            }

            public async Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
            {
                await Task.Delay(PageDelay, cancellationToken).ConfigureAwait(false);
                return await _inner.ReadPageAsync(offset, pageSize, cancellationToken).ConfigureAwait(false);
            }

            public async Task<IReadOnlyList<DeleteResult>> DeleteAsync(
                IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken)
            {
                await Task.Delay(BatchDelay, CancellationToken.None).ConfigureAwait(false);
                return await _inner.DeleteAsync(itemIds, mode, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
