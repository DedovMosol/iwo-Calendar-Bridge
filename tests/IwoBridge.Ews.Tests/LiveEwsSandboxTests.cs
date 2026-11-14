using System;
using System.Linq;
using System.Text;
using System.Threading;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.InMemory;
using Microsoft.Exchange.WebServices.Data;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Песочница по документации живых проверок: временная папка календаря на настоящем Exchange 2007,
    /// основной ящик не пишется, папка в конце удаляется. Нужны <c>IWO_LIVE_*</c> (см. <see cref="LiveFactAttribute"/>).
    /// Локального Exchange 2007 нет и не будет — SOAP 2007 проверяется только так.
    /// </summary>
    public sealed class LiveEwsSandboxTests
    {
        private readonly ITestOutputHelper _output;

        public LiveEwsSandboxTests(ITestOutputHelper output) => _output = output;

        [LiveFact]
        public async Task WeeklyFrom1990_ClippedToMonth_NoHistory_ExceptionLocation_RtfPictCid()
        {
            (var profile, var credentials) = LiveFactAttribute.Settings();
            TargetTimeZone zone = profile.TimeZone;
            using (var root = new EwsTarget(profile, credentials))
            {
                CancellationToken ct = CancellationToken.None;
                string name = "iwo-bridge-clip-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                FolderId parent = EwsCalendarGateway.CalendarOf(profile.MailboxAddress);
                FolderId folder = await root.ReadChannel.RunAsync(
                    service =>
                    {
                        var created = new CalendarFolder(service) { DisplayName = name };
                        created.Save(parent);
                        return created.Id;
                    },
                    ct);
                _output.WriteLine("Песочница: " + name);
                try
                {
                    using (var target = new EwsTarget(profile, credentials, folder))
                    {
                        DateTime todayLocal = zone.UtcToLocal(DateTime.UtcNow).Date;
                        DateTime monthStart = new DateTime(todayLocal.Year, todayLocal.Month, 1);
                        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
                        (DateTime from, DateTime to) = zone.PeriodFromLocalDates(monthStart, monthEnd);

                        DateTime originLocal = new DateTime(1990, 3, 14, 8, 0, 0); // среда
                        DateTime originUtc = zone.LocalToUtc(originLocal);
                        var rule = new RecurrenceRule(
                            RecurrencePattern.Weekly, 1, originLocal.Date, days: WeekDays.Wednesday);
                        DateTime firstInMonth = RecurrenceClip.FirstOnOrAfter(rule, monthStart)
                            ?? throw new InvalidOperationException("нет среды в месяце");
                        DateTime firstUtc = zone.LocalToUtc(firstInMonth.AddHours(8));
                        DateTime movedUtc = firstUtc.AddHours(2);

                        byte[] png = Convert.FromBase64String(
                            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
                        byte[] rtf = Encoding.ASCII.GetBytes(
                            @"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fswiss\fcharset204 Arial;}}\f0\fs20 текст{\pict\wmetafile8\picw1\pich1 0100}}");

                        var series = new SourceItem(
                            "live-clip-1990",
                            "Live: клип 1990",
                            "Зал 1990",
                            "текст",
                            originUtc,
                            originUtc.AddHours(1),
                            recurrence: rule,
                            exceptions: new[]
                            {
                                new SourceException(firstUtc, false, movedUtc, movedUtc.AddHours(1), "Live: клип 1990", "Актовый зал", "текст"),
                            },
                            attachments: new[] { new SyncAttachment("pixel.png", png.Length) },
                            rtfCompressed: MapiRtf.FromRtf(rtf));

                        var source = new InMemorySource("песочница", Array.Empty<SourceItem>());
                        source.Put(series, new[] { new AttachmentContent("pixel.png", png, "live-pixel") });

                        var runner = new CopyRunner(source, target.CopyTarget, EwsTarget.StopsBatch);
                        CopyPreview preview = await runner.PreviewAsync(from, to, false, null, ct);
                        Assert.Equal(1, preview.Plan.Count(SyncActionKind.Create));
                        CopyResult result = await runner.RunAsync(preview, preview.Plan.Actions, null, ct);
                        Assert.True(result.Fatal is null, result.Fatal is null ? "" : EwsTarget.Explain(result.Fatal));
                        Assert.Empty(result.Failures);
                        Assert.Equal(1, result.Created);

                        CopyPreview again = await runner.PreviewAsync(from, to, false, null, ct);
                        Assert.True(again.Plan.IsEmpty, "повтор должен быть пуст: " + again.Plan.UnchangedCount);

                        SourceRead written = await target.WrittenCopies.ReadAsync(from, to, Array.Empty<string>(), null, ct);
                        SourceItem got = Assert.Single(written.Items);
                        Assert.Equal("Live: клип 1990", got.Subject);
                        Assert.NotNull(got.Recurrence);
                        Assert.Equal(firstInMonth, got.Recurrence!.StartDate);
                        Assert.NotEqual(originLocal.Date, got.Recurrence.StartDate);
                        Assert.Equal(firstUtc, got.StartUtc);
                        Assert.Equal(firstUtc, got.StartUtc);
                        Assert.True(got.Recurrence.NeverEnds);
                        SourceException changed = Assert.Single(got.Exceptions, e => !e.IsDeleted);
                        Assert.Equal("Актовый зал", changed.Location);
                        Assert.NotNull(got.RtfCompressed);
                        Assert.Contains("текст", got.Body);
                        var files = await target.WrittenCopies.LoadAttachmentsAsync(got, ct);
                        AttachmentContent pixel = Assert.Single(files);
                        Assert.Equal("pixel.png", pixel.Name);
                        Assert.Equal("live-pixel", pixel.ContentId);

                        int before = await CountInViewAsync(target, folder, from.AddDays(-400), from, ct);
                        int inside = await CountInViewAsync(target, folder, from, to, ct);
                        _output.WriteLine($"CalendarView до окна: {before}, в окне: {inside}; правило с {got.Recurrence.StartDate:yyyy-MM-dd}");
                        Assert.Equal(0, before);
                        Assert.True(inside >= 1, "в месяце должно быть хотя бы одно вхождение");
                    }
                }
                finally
                {
                    await root.ReadChannel.RunAsync(service => Folder.Bind(service, folder).Delete(DeleteMode.HardDelete), ct);
                    _output.WriteLine("Песочница удалена.");
                }
            }
        }

        private static System.Threading.Tasks.Task<int> CountInViewAsync(EwsTarget target, FolderId folder, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            return target.ReadChannel.RunAsync(
                service =>
                {
                    var view = new CalendarView(startUtc, endUtc, 1000)
                    {
                        PropertySet = new PropertySet(BasePropertySet.IdOnly, ItemSchema.Subject),
                    };
                    FindItemsResults<Appointment> found = service.FindAppointments(folder, view);
                    if (found.MoreAvailable)
                    {
                        throw new InvalidOperationException("CalendarView усечён — клип 1990 нельзя подтвердить.");
                    }

                    return found.Items.Count(a =>
                        a.TryGetProperty(ItemSchema.Subject, out string s) && s == "Live: клип 1990");
                },
                ct);
        }
    }
}
