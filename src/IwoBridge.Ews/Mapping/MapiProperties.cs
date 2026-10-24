using Microsoft.Exchange.WebServices.Data;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>Свойства MAPI описания с оформлением — одно место для чтения источника и записи копии (DRY).</summary>
    internal static class MapiProperties
    {
        /// <summary>PR_RTF_COMPRESSED: описание в RTF (шрифты, таблицы, вставленные картинки).</summary>
        public static readonly ExtendedPropertyDefinition RtfCompressed = new ExtendedPropertyDefinition(0x1009, MapiPropertyType.Binary);

        /// <summary>PR_RTF_IN_SYNC: RTF — основное описание, текст и HTML строятся из него.</summary>
        public static readonly ExtendedPropertyDefinition RtfInSync = new ExtendedPropertyDefinition(0x0E1F, MapiPropertyType.Boolean);
    }
}
