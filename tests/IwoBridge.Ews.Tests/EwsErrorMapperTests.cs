using System;
using System.Net;
using IwoBridge.Core.Errors;
using IwoBridge.Ews.Mapping;
using Microsoft.Exchange.WebServices.Data;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Коды ошибок EWS и транспортные исключения → виды ошибок ядра, по которым ErrorClassifier решает про повторы.
    /// </summary>
    public sealed class EwsErrorMapperTests
    {
        [Theory]
        [InlineData(ServiceError.ErrorAccessDenied, OperationErrorKind.AccessDenied)]
        [InlineData(ServiceError.ErrorNonExistentMailbox, OperationErrorKind.MailboxNotFound)]
        [InlineData(ServiceError.ErrorFolderNotFound, OperationErrorKind.AccessDenied)]
        [InlineData(ServiceError.ErrorServerBusy, OperationErrorKind.ServerBusy)]
        [InlineData(ServiceError.ErrorMailboxMoveInProgress, OperationErrorKind.ServerBusy)]
        [InlineData(ServiceError.ErrorMailboxStoreUnavailable, OperationErrorKind.ServerBusy)]
        [InlineData(ServiceError.ErrorInternalServerTransientError, OperationErrorKind.ServerBusy)]
        [InlineData(ServiceError.ErrorTimeoutExpired, OperationErrorKind.Timeout)]
        [InlineData(ServiceError.ErrorConnectionFailed, OperationErrorKind.ConnectionReset)]
        [InlineData(ServiceError.ErrorExceededFindCountLimit, OperationErrorKind.RequestTooLarge)]
        [InlineData(ServiceError.ErrorCalendarViewRangeTooBig, OperationErrorKind.RequestTooLarge)]
        [InlineData(ServiceError.ErrorQuotaExceeded, OperationErrorKind.QuotaExceeded)]
        [InlineData(ServiceError.ErrorIrresolvableConflict, OperationErrorKind.VersionConflict)]
        [InlineData(ServiceError.ErrorSchemaValidation, OperationErrorKind.SchemaError)]
        [InlineData(ServiceError.NoError, OperationErrorKind.Unknown)]
        public void Map_MapsKnownCodes(ServiceError error, OperationErrorKind expected)
        {
            Assert.Equal(expected, EwsErrorMapper.Map(error));
        }

        [Theory]
        [InlineData(WebExceptionStatus.Timeout, OperationErrorKind.Timeout)]
        [InlineData(WebExceptionStatus.ConnectFailure, OperationErrorKind.ConnectionReset)]
        [InlineData(WebExceptionStatus.NameResolutionFailure, OperationErrorKind.ConnectionReset)]
        [InlineData(WebExceptionStatus.ReceiveFailure, OperationErrorKind.ConnectionReset)]
        [InlineData(WebExceptionStatus.TrustFailure, OperationErrorKind.CertificatePinMismatch)]
        [InlineData(WebExceptionStatus.SecureChannelFailure, OperationErrorKind.TlsNegotiationFailed)]
        public void MapException_WebExceptionStatus(WebExceptionStatus status, OperationErrorKind expected)
        {
            Assert.Equal(expected, EwsErrorMapper.MapException(new WebException("тест", status)));
        }

        [Fact]
        public void MapException_RequestWrappingWebException_UsesInner()
        {
            // EWS Managed API оборачивает транспортную ошибку в ServiceRequestException.
            var wrapped = new ServiceRequestException("тест", new WebException("тест", WebExceptionStatus.TrustFailure));

            Assert.Equal(OperationErrorKind.CertificatePinMismatch, EwsErrorMapper.MapException(wrapped));
        }

        [Fact]
        public void MapException_SocketAndIo_AreConnectionReset()
        {
            Assert.Equal(OperationErrorKind.ConnectionReset, EwsErrorMapper.MapException(new System.Net.Sockets.SocketException()));
            Assert.Equal(OperationErrorKind.ConnectionReset,
                EwsErrorMapper.MapException(new ServiceRequestException("тест", new System.IO.IOException("обрыв"))));
        }

        [Fact]
        public void ServerBackOff_NoHint_IsNull()
        {
            Assert.Null(EwsErrorMapper.ServerBackOff(new TimeoutException()));
        }

        [Fact]
        public void MapException_TimeoutException_IsTimeout()
        {
            Assert.Equal(OperationErrorKind.Timeout, EwsErrorMapper.MapException(new TimeoutException()));
        }

        [Fact]
        public void MapException_Unrecognized_IsUnknown()
        {
            Assert.Equal(OperationErrorKind.Unknown, EwsErrorMapper.MapException(new InvalidOperationException()));
        }
    }
}
