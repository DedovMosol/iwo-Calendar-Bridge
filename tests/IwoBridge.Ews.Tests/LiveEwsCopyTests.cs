using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using IwoBridge.App.Copy;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using Microsoft.Exchange.WebServices.Data;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Живая проверка копирования на настоящем сервере Exchange. Запускается, только если заданы переменные окружения
    /// (иначе пропускается — в CI и у разработчика без сервера):
    ///  - <c>IWO_LIVE_EWS</c> — адрес сервера или EWS;
    ///  - <c>IWO_LIVE_USER</c>, <c>IWO_LIVE_PASSWORD</c> — учётная запись;
    ///  - <c>IWO_LIVE_MAILBOX</c> — ящик (необязательно, иначе ящик учётной записи);
    ///  - <c>IWO_LIVE_OFFSET</c> — пояс календаря, например <c>+03:00</c>;
    ///  - <c>IWO_LIVE_PIN</c> — отпечаток сертификата (необязательно: иначе доверие при первом подключении).
    /// Основной календарь не трогается: в календаре ящика создаётся временная папка, копирование идёт в неё, в конце
    /// папка удаляется окончательно (и при падении проверки тоже).
    /// </summary>
    public sealed class LiveEwsCopyTests
    {
        private readonly ITestOutputHelper _output;

        public LiveEwsCopyTests(ITestOutputHelper output) => _output = output;

        [LiveFact]
        public async Task Copy_ToTempFolder_RoundTrip_Idempotent_UpdateDelete_Recreate()
        {
            (ConnectionProfile profile, CredentialSecret credentials) = LiveFactAttribute.Settings();
            using (var root = new EwsTarget(profile, credentials))
            {
                CancellationToken ct = CancellationToken.None;
                string name = "iwo-bridge-live-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                FolderId parent = EwsCalendarGateway.CalendarOf(profile.MailboxAddress);
                FolderId folder = await root.ReadChannel.RunAsync(
                    service =>
                    {
                        var created = new CalendarFolder(service) { DisplayName = name };
                        created.Save(parent);
                        return created.Id;
                    },
                    ct);
                _output.WriteLine("Временная папка: " + name);
                try
                {
                    using (var target = new EwsTarget(profile, credentials, folder))
                    {
                        await RunScenario(profile.TimeZone.Offset, target, ct);
                    }
                }
                finally
                {
                    await root.ReadChannel.RunAsync(service => Folder.Bind(service, folder).Delete(DeleteMode.HardDelete), ct);
                    _output.WriteLine("Временная папка удалена.");
                }
            }
        }

        /// <summary>Основной календарь ящика как источник — только чтение: сколько событий за два года вокруг сегодня.</summary>
        [LiveFact]
        public async Task Source_MainCalendar_ReadOnly()
        {
            (ConnectionProfile profile, CredentialSecret credentials) = LiveFactAttribute.Settings();
            using (var target = new EwsTarget(profile, credentials))
            {
                DateTime today = DateTime.UtcNow.Date;
                SourceRead read = await target.Source.ReadAsync(today.AddYears(-1), today.AddYears(1), Array.Empty<string>(), null, CancellationToken.None);
                int total = await target.CheckAsync(CancellationToken.None);
                _output.WriteLine($"Элементов в календаре: {total}; событий за год до и после сегодня: {read.Items.Count}; серий: {read.Items.Count(i => i.Recurrence != null)}; неполное: {read.IncompleteReason ?? "нет"}");
                Assert.Null(read.IncompleteReason);

                // «Всё время»: весь календарь.
                (DateTime allFrom, DateTime allTo) = profile.TimeZone.PeriodFromLocalDates(
                    IwoBridge.Core.Time.TargetTimeZone.MinPeriodDate, IwoBridge.Core.Time.TargetTimeZone.MaxPeriodDate);
                SourceRead all = await target.Source.ReadAsync(allFrom, allTo, Array.Empty<string>(), null, CancellationToken.None);
                int withCategories = all.Items.Count(i => i.Categories.Count > 0 || i.Exceptions.Any(e => e.Categories?.Count > 0));
                _output.WriteLine($"За всё время: событий {all.Items.Count}, с категориями {withCategories}; неполное: {all.IncompleteReason ?? "нет"}");
                Assert.Null(all.IncompleteReason);
                Assert.True(all.Items.Count >= read.Items.Count);
            }
        }

        private async Task RunScenario(TimeSpan offset, EwsTarget target, CancellationToken ct)
        {
            // Через 10 дней, в UTC; весь день — местная полночь календаря.
            DateTime day = DateTime.UtcNow.Date.AddDays(10);
            DateTime At(int d, int hourUtc) => DateTime.SpecifyKind(day.AddDays(d).AddHours(hourUtc), DateTimeKind.Utc);
            DateTime LocalMidnight(int d) => DateTime.SpecifyKind(day.AddDays(d) - offset, DateTimeKind.Utc);

            byte[] file = Encoding.UTF8.GetBytes("live attachment");
            byte[] second = Encoding.UTF8.GetBytes("second live attachment");
            // Категории — названиями; «;» внутри названия проверяет, что список переносится точно, а не строкой.
            var single = new SourceItem("live-single", "Live: одиночное", "Зал 1", "Описание события", At(0, 6), At(0, 7),
                attachments: new[] { new SyncAttachment("заметка.txt", file.Length), new SyncAttachment("вторая.txt", second.Length) },
                categories: new[] { "Красная категория", "Live; категория" });
            SourceItem AllDay(params string[] categories) => new SourceItem(
                "live-allday", "Live: весь день", null, null, LocalMidnight(1), LocalMidnight(2), isAllDay: true, busy: FreeBusy.OutOfOffice, categories: categories);
            SourceItem allDay = AllDay("Live отпуск");
            var secret = new SourceItem("live-private", "Live: личное содержимое", "Скрытое место", "Скрытое описание", At(1, 9), At(1, 10),
                sensitivity: ItemSensitivity.Private);
            DateTime seriesStart = At(2, 8);
            DateTime localStart = seriesStart + offset;
            var rule = new RecurrenceRule(RecurrencePattern.Weekly, 1, localStart.Date, occurrences: 4,
                days: (WeekDays)(1 << (int)localStart.DayOfWeek));
            // Категории серии; у третьего повторения — свои (или те же, что у серии, — null).
            SourceItem Series(string subjectOfThird, string[]? categoriesOfThird) => new SourceItem(
                "live-series", "Live: серия", "Зал 2", null, seriesStart, seriesStart.AddHours(1),
                recurrence: rule, seriesEndUtc: seriesStart.AddDays(21).AddHours(1),
                exceptions: new[]
                {
                    new SourceException(seriesStart.AddDays(7), true),
                    new SourceException(
                        seriesStart.AddDays(14), false, seriesStart.AddDays(14).AddHours(1), seriesStart.AddDays(14).AddHours(2), subjectOfThird, categories: categoriesOfThird),
                    // Обычная серия, но одно повторение личное: в копию — только время, тема «Личное», без категорий серии.
                    new SourceException(seriesStart.AddDays(21), false, seriesStart.AddDays(21), seriesStart.AddDays(21).AddHours(1), "Live: тайное", "Тайный зал", "Тайное описание", isPrivate: true),
                },
                categories: new[] { "Live серия" });

            // Описание с оформлением — RTF, как его отдаёт Outlook (MapiRtf).
            // Кодировка шрифта — \fcharset204 (кириллица), как у Outlook: без неё байты читаются как западноевропейские.
            byte[] rtfDocument = Encoding.ASCII.GetBytes(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fswiss\fcharset204 Arial;}}\f0\fs20 \b\'e6\'e8\'f0\'ed\'fb\'e9 \'f2\'e5\'ea\'f1\'f2\b0\par}");
            var rich = new SourceItem("live-rich", "Live: оформление", null, "жирный текст", At(3, 6), At(3, 7), rtfCompressed: MapiRtf.FromRtf(rtfDocument));

            var source = new InMemorySource("живой источник", Array.Empty<SourceItem>());
            source.Put(single, new[] { new AttachmentContent("заметка.txt", file), new AttachmentContent("вторая.txt", second) });
            source.Put(allDay);
            source.Put(secret);
            source.Put(Series("Live: перенесённое", new[] { "Синяя категория" }));
            source.Put(rich);

            var runner = new CopyRunner(source, target.CopyTarget, EwsTarget.StopsBatch);
            DateTime from = day.AddDays(-1), to = day.AddDays(40);

            // 1. Первое копирование: всё создаётся.
            await Apply(runner, from, to, ct, expectCreate: 5);

            // 2. Повтор без изменений: делать нечего (отпечаток, записанный в копию, совпадает с источником).
            CopyPreview again = await runner.PreviewAsync(from, to, false, null, ct);
            Assert.True(again.Plan.IsEmpty, Describe(again.Plan));
            Assert.Equal(5, again.Plan.UnchangedCount);

            // 3. Что записано — читаем папку тем же адаптером, что и источник по EWS.
            SourceRead written = await target.WrittenCopies.ReadAsync(from, to, Array.Empty<string>(), null, ct);
            Assert.Null(written.IncompleteReason);

            // Копии моста не видны как источник: при встречной настройке копии копий не появятся.
            Assert.Empty((await target.Source.ReadAsync(from, to, Array.Empty<string>(), null, ct)).Items);
            foreach (SourceItem item in written.Items.OrderBy(i => i.StartUtc))
            {
                _output.WriteLine($"{item.Subject} | {item.StartUtc:u} – {item.EndUtc:u} | весь день: {item.IsAllDay} | {item.Busy} | {item.Sensitivity} | "
                    + $"правило: {item.Recurrence?.Canonical ?? "-"} | исключений: {item.Exceptions.Count} | вложения: {string.Join(", ", item.Attachments.Select(a => a.Name))} | "
                    + $"категории: {string.Join(" / ", item.Categories)}"
                    + string.Concat(item.Exceptions.Where(e => !e.IsDeleted).Select(e => $" | повторение {e.OriginalStartUtc:u}: {(e.Categories is null ? "как у серии" : string.Join(" / ", e.Categories))}")));
            }

            SourceItem gotSingle = written.Items.Single(i => i.Subject == single.Subject);
            Assert.Equal(single.StartUtc, gotSingle.StartUtc);
            Assert.Equal(single.EndUtc, gotSingle.EndUtc);
            Assert.Equal("Зал 1", gotSingle.Location);
            Assert.Contains("Описание события", gotSingle.Body);
            Assert.Equal(new[] { "вторая.txt", "заметка.txt" }, gotSingle.Attachments.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "Красная категория", "Live; категория" }, gotSingle.Categories);
            IReadOnlyList<AttachmentContent> gotFiles = await target.WrittenCopies.LoadAttachmentsAsync(gotSingle, ct);
            Assert.Equal(second, gotFiles.Single(f => f.Name == "вторая.txt").Content);
            AttachmentContent[] gotFile = gotFiles.Where(f => f.Name == "заметка.txt").ToArray();

            // Описание с оформлением: RTF записан и читается, текст из него Exchange построил сам.
            SourceItem gotRich = written.Items.Single(i => i.Subject == rich.Subject);
            Assert.NotNull(gotRich.RtfCompressed);
            Assert.Contains("жирный текст", gotRich.Body);
            Assert.Equal(file, gotFile.Single().Content);

            SourceItem gotAllDay = written.Items.Single(i => i.Subject == allDay.Subject);
            Assert.True(gotAllDay.IsAllDay);
            Assert.Equal(allDay.StartUtc, gotAllDay.StartUtc);
            Assert.Equal(allDay.EndUtc, gotAllDay.EndUtc);
            Assert.Equal(FreeBusy.OutOfOffice, gotAllDay.Busy);
            Assert.Equal(new[] { "Live отпуск" }, gotAllDay.Categories);

            SourceItem gotSecret = written.Items.Single(i => i.Sensitivity == ItemSensitivity.Private);
            Assert.Equal(CopyRules.PrivateSubject, gotSecret.Subject);
            Assert.Equal(string.Empty, gotSecret.Location);
            Assert.DoesNotContain("Скрытое", gotSecret.Body);

            SourceItem gotSeries = written.Items.Single(i => i.Subject == "Live: серия");
            Assert.NotNull(gotSeries.Recurrence);
            Assert.Equal(RecurrencePattern.Weekly, gotSeries.Recurrence!.Pattern);
            Assert.Equal(4, gotSeries.Recurrence.Occurrences ?? 0);
            Assert.Equal(seriesStart, gotSeries.StartUtc);
            Assert.Contains(gotSeries.Exceptions, e => e.IsDeleted && e.OriginalStartUtc == seriesStart.AddDays(7));
            SourceException gotThird = gotSeries.Exceptions.Single(e => !e.IsDeleted && e.Subject == "Live: перенесённое");
            Assert.Equal(seriesStart.AddDays(14).AddHours(1), gotThird.StartUtc);
            Assert.Equal(new[] { "Live серия" }, gotSeries.Categories);
            Assert.Equal(new[] { "Синяя категория" }, gotThird.Categories);
            SourceException gotPrivate = gotSeries.Exceptions.Single(e => e.OriginalStartUtc == seriesStart.AddDays(21));
            Assert.True(gotPrivate.IsPrivate);
            Assert.Equal(CopyRules.PrivateSubject, gotPrivate.Subject);
            Assert.DoesNotContain("Тайн", (gotPrivate.Location ?? string.Empty) + (gotPrivate.Body ?? string.Empty));
            Assert.Null(gotPrivate.Categories); // свои категории скрытого повторения не перенесены, у него категории серии

            // 4. Правка и удаление в источнике: две копии перезаписываются (у одной категории сменились, у другой сняты),
            // одна удаляется.
            source.Put(new SourceItem("live-single", "Live: одиночное (изменено)", "Зал 3", "Новое описание", At(0, 6), At(0, 8), categories: new[] { "Live проект" }));
            source.Put(AllDay());
            source.Remove("live-private");
            await Apply(runner, from, to, ct, expectUpdate: 2, expectDelete: 1);

            // 5. Изменённое исключение серии: серия пересоздаётся; у повторения теперь категории серии.
            source.Put(Series("Live: перенесённое ещё раз", null));
            await Apply(runner, from, to, ct, expectUpdate: 1);

            CopyPreview last = await runner.PreviewAsync(from, to, false, null, ct);
            Assert.True(last.Plan.IsEmpty, Describe(last.Plan));

            // «Всё время» — весь допустимый диапазон дат: тот же итог, сервер такой период принимает.
            (DateTime allFrom, DateTime allTo) = IwoBridge.Core.Time.TargetTimeZone.FromOffset(offset)
                .PeriodFromLocalDates(IwoBridge.Core.Time.TargetTimeZone.MinPeriodDate, IwoBridge.Core.Time.TargetTimeZone.MaxPeriodDate);
            CopyPreview allTime = await runner.PreviewAsync(allFrom, allTo, false, null, ct);
            Assert.True(allTime.Plan.IsEmpty, Describe(allTime.Plan));
            Assert.Equal(4, allTime.Plan.UnchangedCount);
            SourceRead final = await target.WrittenCopies.ReadAsync(from, to, Array.Empty<string>(), null, ct);
            Assert.Equal(
                new[] { "Live: весь день", "Live: одиночное (изменено)", "Live: оформление", "Live: серия" },
                final.Items.Select(i => i.Subject).OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal(new[] { "Live проект" }, final.Items.Single(i => i.Subject == "Live: одиночное (изменено)").Categories);
            Assert.Empty(final.Items.Single(i => i.Subject == "Live: весь день").Categories);
            SourceException finalThird = final.Items.Single(i => i.Subject == "Live: серия").Exceptions.Single(e => e.Subject == "Live: перенесённое ещё раз");
            Assert.Null(finalThird.Categories); // те же, что у серии

            // 6. У повторения в источнике категории сняты, у серии остались: снять их у одного повторения сервер не даёт —
            // копия записана с замечанием, следующий запуск её не трогает.
            source.Put(Series("Live: без категорий", Array.Empty<string>()));
            CopyResult cleared = await Apply(runner, from, to, ct, expectUpdate: 1);
            string remark = Assert.Single(cleared.Remarks);
            Assert.Contains("остались категории серии", remark);
            CopyPreview afterRemark = await runner.PreviewAsync(from, to, false, null, ct);
            Assert.True(afterRemark.Plan.IsEmpty, Describe(afterRemark.Plan));
        }

        private async System.Threading.Tasks.Task<CopyResult> Apply(CopyRunner runner, DateTime from, DateTime to, CancellationToken ct, int expectCreate = 0, int expectUpdate = 0, int expectDelete = 0)
        {
            CopyPreview preview = await runner.PreviewAsync(from, to, false, null, ct);
            _output.WriteLine("План: " + Describe(preview.Plan));
            CopyResult result = await runner.RunAsync(preview, preview.Plan.Actions, null, ct);
            string failures = string.Join("; ", result.Failures.Select(f => (f.Action.Source?.Subject ?? f.Action.Copy?.Subject) + ": " + EwsTarget.Explain(f.Exception)));
            Assert.True(result.Fatal is null, result.Fatal is null ? null : EwsTarget.Explain(result.Fatal));
            Assert.True(result.Failures.Count == 0, failures);
            Assert.Null(result.RefusedReason);
            Assert.Equal(expectCreate, result.Created);
            Assert.Equal(expectUpdate, result.Updated);
            Assert.Equal(expectDelete, result.Deleted);
            return result;
        }

        private static string Describe(SyncPlan plan) => string.Join(", ", plan.Actions.Select(a => a.Kind + " " + a.SourceId)) + $" (без изменений {plan.UnchangedCount})";
    }

    /// <summary>Живая проверка: выполняется, только если заданы переменные окружения <c>IWO_LIVE_*</c>.</summary>
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IWO_LIVE_EWS")))
            {
                Skip = "Живой сервер не задан (IWO_LIVE_EWS).";
            }
        }

        public static (ConnectionProfile Profile, CredentialSecret Credentials) Settings()
        {
            string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;
            string url = Env("IWO_LIVE_EWS");
            if (!url.Contains("://"))
            {
                url = "https://" + url;
            }

            if (!url.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase))
            {
                url = url.TrimEnd('/') + "/EWS/Exchange.asmx";
            }

            string offsetText = Env("IWO_LIVE_OFFSET");
            TimeSpan offset = TimeSpan.FromHours(3);
            if (!string.IsNullOrEmpty(offsetText))
            {
                offset = TimeSpan.ParseExact(offsetText.TrimStart('+', '-'), @"hh\:mm", CultureInfo.InvariantCulture);
                if (offsetText.StartsWith("-", StringComparison.Ordinal))
                {
                    offset = offset.Negate();
                }
            }
            string mailbox = Env("IWO_LIVE_MAILBOX");
            string pin = Env("IWO_LIVE_PIN");
            return (
                new ConnectionProfile(new Uri(url), mailbox.Length == 0 ? null : mailbox, offset, pin.Length == 0 ? null : pin),
                new CredentialSecret(Env("IWO_LIVE_USER"), Env("IWO_LIVE_PASSWORD")));
        }
    }
}
