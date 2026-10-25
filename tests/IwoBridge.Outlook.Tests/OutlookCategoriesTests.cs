using Xunit;

namespace IwoBridge.Outlook.Tests
{
    /// <summary>
    /// Запасной путь чтения категорий Outlook — строка Categories: названия через разделитель списка Windows, с пробелами
    /// после разделителя, без пустых и повторов.
    /// </summary>
    public sealed class OutlookCategoriesTests
    {
        [Fact]
        public void Split_RussianSeparator_TrimsAndDropsRepeats()
        {
            Assert.Equal(
                new[] { "Красная категория", "Проект, этап 2" },
                OutlookSource.SplitCategories("Красная категория; Проект, этап 2;;красная категория ", ";"));
        }

        [Fact]
        public void Split_EnglishSeparator_AndDefaults()
        {
            Assert.Equal(new[] { "Red Category", "Project" }, OutlookSource.SplitCategories("Red Category, Project", ","));
            Assert.Equal(new[] { "a", "b" }, OutlookSource.SplitCategories("a;b", null));
            Assert.Empty(OutlookSource.SplitCategories(null, ";"));
        }
    }
}
