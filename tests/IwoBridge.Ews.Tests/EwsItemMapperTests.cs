using System;
using IwoBridge.Core.Model;
using IwoBridge.Ews.Mapping;
using Microsoft.Exchange.WebServices.Data;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Чистое отображение полей EWS в модель: вид элемента, границы серии только у мастера, признак экземпляра
    /// серии только у повторения и исключения, тема — как есть (пустая → null).
    /// </summary>
    public sealed class EwsItemMapperTests
    {
        private static readonly DateTime Start = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime End = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime First = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Last = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Master_KeepsOccurrenceBounds()
        {
            TargetItemInfo item = EwsItemMapper.ToTargetItem(
                "id1", "ck", "IPM.Appointment", isRecurringMaster: true, Start, End, First, Last, null, "Серия");

            Assert.Equal(ItemKind.RecurringMaster, item.Kind);
            Assert.Equal(First, item.FirstOccurrenceUtc);
            Assert.Equal(Last, item.LastOccurrenceUtc);
            Assert.Equal("Серия", item.Subject);
        }

        [Fact]
        public void Single_DropsOccurrenceBounds()
        {
            TargetItemInfo item = EwsItemMapper.ToTargetItem(
                "id2", "ck", "IPM.Appointment", isRecurringMaster: false, Start, End, First, Last, "src-1", null);

            Assert.Equal(ItemKind.Single, item.Kind);
            Assert.Null(item.FirstOccurrenceUtc);
            Assert.Null(item.LastOccurrenceUtc);
            Assert.True(item.IsBridgeCopy);
            Assert.Null(item.Subject);
        }

        [Fact]
        public void EmptySubject_BecomesNull()
        {
            TargetItemInfo item = EwsItemMapper.ToTargetItem(
                "id3", "ck", "IPM.Appointment", isRecurringMaster: false, Start, End, null, null, null, string.Empty);

            Assert.Null(item.Subject);
        }

        [Theory]
        [InlineData(true, ItemKind.RecurringMaster)]
        [InlineData(false, ItemKind.Single)]
        public void KindFromRecurringMaster_Maps(bool isMaster, ItemKind expected)
        {
            Assert.Equal(expected, EwsItemMapper.KindFromRecurringMaster(isMaster));
        }

        [Theory]
        [InlineData(AppointmentType.Occurrence, true)]
        [InlineData(AppointmentType.Exception, true)]
        [InlineData(AppointmentType.Single, false)]
        public void ToOccurrence_MarksOnlySeriesInstances(AppointmentType type, bool expected)
        {
            TargetOccurrence occurrence = EwsItemMapper.ToOccurrence("occ1", "uid-1", Start, End, type, "Тема");

            Assert.Equal(expected, occurrence.IsSeriesInstance);
            Assert.Equal(Start, occurrence.StartUtc);
            Assert.Equal(End, occurrence.EndUtc);
            Assert.Equal("uid-1", occurrence.SeriesUid);
            Assert.Equal("Тема", occurrence.Subject);
        }
    }
}
