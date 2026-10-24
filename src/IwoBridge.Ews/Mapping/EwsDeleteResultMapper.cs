using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Paging;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>
    /// Ответ EWS на удаление одного элемента → исход для строгой оценки пакета. «Уже нет» — идемпотентный успех:
    /// повторный запуск очистки после обрыва не должен считаться ошибкой.
    /// </summary>
    public static class EwsDeleteResultMapper
    {
        public static DeleteResult Map(ServiceResult result, ServiceError errorCode)
        {
            switch (errorCode)
            {
                // Элемента нет. Для экземпляра серии, удалённого ранее, Exchange отвечает отдельным кодом
                // (ErrorCalendarOccurrenceIsDeletedFromRecurrence) — по смыслу то же «уже удалён».
                case ServiceError.ErrorItemNotFound:
                case ServiceError.ErrorCalendarOccurrenceIsDeletedFromRecurrence:
                    return DeleteResult.AlreadyAbsent();
            }

            if (result == ServiceResult.Success)
            {
                return DeleteResult.Removed();
            }

            string code = errorCode == ServiceError.NoError ? result.ToString() : errorCode.ToString();
            return DeleteResult.Failed(code);
        }
    }
}
