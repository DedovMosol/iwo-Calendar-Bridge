using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IwoBridge.App.Copy;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Sync;
using IwoBridge.Outlook;
using Microsoft.Exchange.WebServices.Data;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Живая сквозная проверка главного сценария: календарь, открытый сейчас в Outlook этого компьютера (например, общий
    /// календарь коллеги), → копия по EWS. Нужны и <c>IWO_LIVE_EWS…</c> (см. <see cref="LiveFactAttribute"/>), и
    /// <c>IWO_LIVE_OUTLOOK=1</c>. Копия пишется во временную папку календаря ящика-приёмника, которая в конце удаляется
    /// окончательно; основной календарь и источник не меняются.
    /// </summary>
    public sealed class LiveOutlookToEwsTests
    {
        private readonly ITestOutputHelper _output;

        public LiveOutlookToEwsTests(ITestOutputHelper output) => _output = output;

        [LiveFact]
        public async Task CurrentOutlookCalendar_CopiedToTempFolder_Complete_Idempotent()
        {
            if (Environment.GetEnvironmentVariable("IWO_LIVE_OUTLOOK") != "1")
            {
                return;
            }

            (ConnectionProfile profile, CredentialSecret credentials) = LiveFactAttribute.Settings();
            using (var outlook = new OutlookSession())
            using (var root = new EwsTarget(profile, credentials))
            {
                IReadOnlyList<OutlookCalendarRef> calendars = await outlook.ListCalendarsAsync();
                OutlookCalendarRef current = calendars.Single(c => c.IsCurrent);
                ISourceCalendar source = outlook.Open(current);


                CancellationToken ct = CancellationToken.None;
                FolderId parent = EwsCalendarGateway.CalendarOf(profile.MailboxAddress);
                string name = "iwo-bridge-live-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                FolderId folder = await root.ReadChannel.RunAsync(
                    service =>
                    {
                        var created = new CalendarFolder(service) { DisplayName = name };
                        created.Save(parent);
                        return created.Id;
                    },
                    ct);
                try
                {
                    using (var target = new EwsTarget(profile, credentials, folder))
                    {
                        var runner = new CopyRunner(source, target.CopyTarget, EwsTarget.StopsBatch);
                        DateTime today = DateTime.UtcNow.Date;
                        DateTime from = today.AddMonths(-1), to = today.AddMonths(3);

                        var watch = Stopwatch.StartNew();
                        CopyPreview preview = await runner.PreviewAsync(from, to, false, null, ct);
                        _output.WriteLine($"Предпросмотр: создать {preview.Plan.Count(SyncActionKind.Create)}, замечаний {preview.Warnings.Count}, неполное: {(preview.Plan.DeletesSuppressedReason is null ? "нет" : "да")}; {watch.Elapsed.TotalSeconds:0} с");
                        Assert.True(preview.Plan.DeletesSuppressedReason is null, "источник прочитан не полностью");
                        Assert.True(preview.Plan.CanApply, preview.Plan.BlockReason);

                        watch.Restart();
                        CopyResult result = await runner.RunAsync(preview, preview.Plan.Actions, null, ct);
                        _output.WriteLine($"Записано: создано {result.Created}, сбоев {result.Failures.Count}; {watch.Elapsed.TotalSeconds:0} с");
                        // Темы событий — служебные данные: в вывод попадает только вид ошибки.
                        foreach (CopyFailure failure in result.Failures.Take(10))
                        {
                            _output.WriteLine("  сбой: " + EwsTarget.Classify(failure.Exception));
                        }

                        Assert.Null(result.Fatal);
                        Assert.Empty(result.Failures);
                        Assert.Equal(preview.Plan.Actions.Count, result.Created);

                        // Повтор: делать нечего — копирование из Outlook идемпотентно и на настоящих данных.
                        CopyPreview again = await runner.PreviewAsync(from, to, false, null, ct);
                        _output.WriteLine($"Повторный предпросмотр: действий {again.Plan.Actions.Count}, без изменений {again.Plan.UnchangedCount}");
                        Assert.True(again.Plan.IsEmpty, string.Join("; ", again.Plan.Actions.Take(10).Select(a => a.Kind + " " + a.Reason)));

                        // Что лежит в папке-копии: то же число событий и те же темы, что в источнике за период.
                        SourceRead written = await target.WrittenCopies.ReadAsync(from, to, Array.Empty<string>(), null, ct);
                        SourceRead original = await source.ReadAsync(from, to, Array.Empty<string>(), null, ct);
                        // Exchange хранит перевод строки в теме как \n (Outlook отдаёт \r\n) — сравнение без этой разницы.
                        string Norm(string s) => s.Replace("\r\n", "\n");
                        string[] expected = original.Items.Select(CopyRules.Prepare).Select(i => Norm(i.Subject)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                        string[] actual = written.Items.Select(i => Norm(i.Subject)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                        _output.WriteLine($"В источнике {expected.Length}, в копии {actual.Length}; серий в копии {written.Items.Count(i => i.Recurrence != null)}, с вложениями {written.Items.Count(i => i.Attachments.Count > 0)}");
                        // Не Assert.Equal: при расхождении он напечатал бы темы событий.
                        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal), "темы копий не совпадают с источником");
                    }
                }
                finally
                {
                    await root.ReadChannel.RunAsync(service => Folder.Bind(service, folder).Delete(DeleteMode.HardDelete), ct);
                    _output.WriteLine("Временная папка удалена.");
                }
            }
        }
    }
}
