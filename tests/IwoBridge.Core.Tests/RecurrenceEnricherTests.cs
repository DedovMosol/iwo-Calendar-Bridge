using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Reading;
using Xunit;
using static IwoBridge.Core.Tests.TestData;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Обогащение снимка данными серий: мастер получает границы, UID и изменённые повторения, остальные поля
    /// (включая тему) сохраняются; одиночные события и мастера без данных не меняются.
    /// </summary>
    public sealed class RecurrenceEnricherTests
    {
        private static TargetItemInfo MasterWithoutBounds(string id)
        {
            return new TargetItemInfo(
                id, "ck", "IPM.Appointment", ItemKind.RecurringMaster,
                Utc(2026, 1, 1, 9), Utc(2026, 1, 1, 10), null, null, null);
        }

        [Fact]
        public void Master_GetsBoundsUidAndModifiedOccurrences()
        {
            var modified = new[] { new OccurrenceSpan(Utc(2026, 1, 25, 9), Utc(2026, 1, 25, 10)) };
            var info = new Dictionary<string, RecurrenceInfo>
            {
                ["m1"] = new RecurrenceInfo(Utc(2026, 1, 1, 9), Utc(2026, 3, 1, 9), "uid-1", modified),
            };

            TargetItemInfo master = RecurrenceEnricher.Apply(new[] { MasterWithoutBounds("m1") }, info).Single();

            Assert.Equal(Utc(2026, 1, 1, 9), master.FirstOccurrenceUtc);
            Assert.Equal(Utc(2026, 3, 1, 9), master.LastOccurrenceUtc);
            Assert.Equal("uid-1", master.Uid);
            Assert.Equal(Utc(2026, 1, 25, 9), master.ModifiedOccurrences.Single().StartUtc);
        }

        [Fact]
        public void Master_KeepsFieldsNotFromRecurrence()
        {
            // Обогащение пересобирает мастер; поля, которых нет в данных серии, не должны теряться.
            var master = new TargetItemInfo(
                "m1", "ck-1", "IPM.Appointment.Custom", ItemKind.RecurringMaster,
                Utc(2026, 1, 1, 9), Utc(2026, 1, 1, 10), null, null, "src-1", subject: "Планёрка");
            var info = new Dictionary<string, RecurrenceInfo>
            {
                ["m1"] = new RecurrenceInfo(Utc(2026, 1, 1, 9), Utc(2026, 3, 1, 9), "uid-1"),
            };

            TargetItemInfo enriched = RecurrenceEnricher.Apply(new[] { master }, info).Single();

            Assert.Equal("ck-1", enriched.ChangeKey);
            Assert.Equal("IPM.Appointment.Custom", enriched.ItemClass);
            Assert.Equal(Utc(2026, 1, 1, 9), enriched.StartUtc);
            Assert.Equal(Utc(2026, 1, 1, 10), enriched.EndUtc);
            Assert.Equal("src-1", enriched.BridgeSourceId);
            Assert.Equal("Планёрка", enriched.Subject);
        }

        [Fact]
        public void Master_NeverEnding_LastStaysNull()
        {
            var info = new Dictionary<string, RecurrenceInfo>
            {
                ["m1"] = new RecurrenceInfo(Utc(2026, 1, 1, 9), null, "uid-1"),
            };

            TargetItemInfo master = RecurrenceEnricher.Apply(new[] { MasterWithoutBounds("m1") }, info).Single();

            Assert.Null(master.LastOccurrenceUtc);
            Assert.Empty(master.ModifiedOccurrences);
        }

        [Fact]
        public void Single_Untouched()
        {
            var items = new[] { Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 1)) };

            IReadOnlyList<TargetItemInfo> result = RecurrenceEnricher.Apply(items, new Dictionary<string, RecurrenceInfo>());

            Assert.Null(result.Single().Uid);
            Assert.Equal(ItemKind.Single, result.Single().Kind);
        }

        [Fact]
        public void Master_WithoutEntry_Untouched()
        {
            IReadOnlyList<TargetItemInfo> result = RecurrenceEnricher.Apply(
                new[] { MasterWithoutBounds("m1") }, new Dictionary<string, RecurrenceInfo>());

            Assert.Null(result.Single().FirstOccurrenceUtc);
            Assert.Null(result.Single().Uid);
        }
    }
}
