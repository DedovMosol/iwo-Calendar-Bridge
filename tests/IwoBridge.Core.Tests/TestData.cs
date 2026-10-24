using System;
using System.Collections.Generic;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Tests
{
    /// <summary>Синтетические данные для тестов — без каких-либо реальных значений.</summary>
    internal static class TestData
    {
        public static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0)
        {
            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        }

        public static TargetItemInfo Single(
            string id,
            DateTime startUtc,
            DateTime endUtc,
            string itemClass = "IPM.Appointment",
            string? bridgeSourceId = null)
        {
            return new TargetItemInfo(
                id, "CK-" + id, itemClass, ItemKind.Single, startUtc, endUtc, null, null, bridgeSourceId);
        }

        /// <summary>Мастер серии: повторения длятся час, первое начинается в <paramref name="firstOccurrenceUtc"/>.</summary>
        public static TargetItemInfo Master(
            string id,
            DateTime firstOccurrenceUtc,
            DateTime? lastOccurrenceUtc,
            string itemClass = "IPM.Appointment",
            string? bridgeSourceId = null,
            string? uid = null,
            IReadOnlyList<OccurrenceSpan>? modified = null)
        {
            return new TargetItemInfo(
                id,
                "CK-" + id,
                itemClass,
                ItemKind.RecurringMaster,
                firstOccurrenceUtc,
                firstOccurrenceUtc.AddHours(1),
                firstOccurrenceUtc,
                lastOccurrenceUtc,
                bridgeSourceId,
                uid,
                modified);
        }

        /// <summary>Экземпляр серии из раскрытого окна (повторение или исключение), длительностью час.</summary>
        public static TargetOccurrence Instance(string id, string uid, DateTime startUtc)
        {
            return new TargetOccurrence(id, uid, startUtc, startUtc.AddHours(1), isSeriesInstance: true);
        }

        /// <summary>Экземпляр серии из раскрытого окна с явным концом.</summary>
        public static TargetOccurrence Instance(string id, string uid, DateTime startUtc, DateTime endUtc)
        {
            return new TargetOccurrence(id, uid, startUtc, endUtc, isSeriesInstance: true);
        }

        /// <summary>Одиночное событие из раскрытого окна.</summary>
        public static TargetOccurrence SingleInWindow(string id, string? uid, DateTime startUtc)
        {
            return new TargetOccurrence(id, uid, startUtc, startUtc.AddHours(1), isSeriesInstance: false);
        }
    }
}
