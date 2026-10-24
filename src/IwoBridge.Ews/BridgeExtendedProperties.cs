using System;
using Microsoft.Exchange.WebServices.Data;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Именованные расширенные свойства-метки моста. GUID-набор — фиксированная константа продукта
    /// (не данные организации). Используются для опознания копий и защиты от дублей (PLAN.md §5).
    /// </summary>
    internal static class BridgeExtendedProperties
    {
        private static readonly Guid PropertySetId = new Guid("8E2B2E7A-0D2A-4E6E-9E8C-9C2B1A7F3D10");

        public static readonly ExtendedPropertyDefinition SourceId =
            new ExtendedPropertyDefinition(PropertySetId, "IwoBridgeSourceId", MapiPropertyType.String);

        public static readonly ExtendedPropertyDefinition ContentHash =
            new ExtendedPropertyDefinition(PropertySetId, "IwoBridgeContentHash", MapiPropertyType.String);

        public static readonly ExtendedPropertyDefinition JobId =
            new ExtendedPropertyDefinition(PropertySetId, "IwoBridgeJobId", MapiPropertyType.String);
    }
}
