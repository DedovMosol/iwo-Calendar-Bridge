using IwoBridge.Core.Paging;
using IwoBridge.Ews.Mapping;
using Microsoft.Exchange.WebServices.Data;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Ответ EWS на удаление одного элемента → исход: успех — удалён, ErrorItemNotFound — уже отсутствует (идемпотентно), прочее — ошибка с кодом.
    /// </summary>
    public sealed class EwsDeleteResultMapperTests
    {
        [Fact]
        public void Success_IsRemoved()
        {
            DeleteResult r = EwsDeleteResultMapper.Map(ServiceResult.Success, ServiceError.NoError);

            Assert.Equal(DeleteItemStatus.Removed, r.Status);
        }

        [Fact]
        public void ItemNotFound_IsAlreadyAbsent()
        {
            DeleteResult r = EwsDeleteResultMapper.Map(ServiceResult.Error, ServiceError.ErrorItemNotFound);

            Assert.Equal(DeleteItemStatus.AlreadyAbsent, r.Status);
            Assert.True(r.IsAccountedFor);
        }

        [Fact]
        public void OccurrenceAlreadyDeleted_IsAlreadyAbsent()
        {
            // Повторная обрезка серии после обрыва: удалённый экземпляр Exchange отмечает своим кодом.
            DeleteResult r = EwsDeleteResultMapper.Map(ServiceResult.Error, ServiceError.ErrorCalendarOccurrenceIsDeletedFromRecurrence);

            Assert.Equal(DeleteItemStatus.AlreadyAbsent, r.Status);
        }

        [Fact]
        public void OtherError_IsFailedWithCode()
        {
            DeleteResult r = EwsDeleteResultMapper.Map(ServiceResult.Error, ServiceError.ErrorAccessDenied);

            Assert.Equal(DeleteItemStatus.Failed, r.Status);
            Assert.Equal("ErrorAccessDenied", r.ErrorCode);
            Assert.False(r.IsAccountedFor);
        }
    }
}
