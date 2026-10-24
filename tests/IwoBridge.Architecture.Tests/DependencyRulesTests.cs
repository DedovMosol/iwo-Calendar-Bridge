using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Time;
using IwoBridge.Ews;
using IwoBridge.InMemory;
using Xunit;

namespace IwoBridge.Architecture.Tests
{
    /// <summary>
    /// Правила зависимостей между слоями (ARCHITECTURE.md, «Принципы»; PLAN.md §2). Проверяются фактические
    /// ссылки собранных сборок, а не текст .csproj: так ловится и ссылка, пришедшая транзитивно или через пакет.
    ///
    /// Каждый продуктовый проект имеет белый список допустимых ссылок:
    ///  - ядро <c>IwoBridge.Core</c> — только базовые сборки .NET Framework, никаких EWS, Outlook, сети и UI;
    ///  - сценарии и ин-мемори адаптер — ядро и базовые сборки;
    ///  - адаптер EWS — ядро и EWS Managed API; это единственное место, где разрешён EWS.
    /// Новая ссылка требует осознанного решения: правка списка здесь + запись в DECISIONS.md.
    ///
    /// Если в <c>src</c> появился проект без правила (например, будущий GUI), тест падает — правило надо добавить.
    /// </summary>
    public sealed class DependencyRulesTests
    {
        private const string ProductPrefix = "IwoBridge.";

        /// <summary>Базовые сборки .NET Framework, допустимые везде.</summary>
        private static readonly string[] Framework = { "mscorlib", "System", "System.Core" };

        /// <summary>Сборка → допустимые ссылки (белый список).</summary>
        private static readonly IReadOnlyDictionary<string, ISet<string>> Allowed = new Dictionary<string, ISet<string>>
        {
            ["IwoBridge.Core"] = Set(Framework),

            // System.Runtime.Serialization и System.Xml — штатный разбор JSON файла настроек (ConnectionProfileFile).
            ["IwoBridge.App"] = Set(Framework, "IwoBridge.Core", "System.Runtime.Serialization", "System.Xml"),

            ["IwoBridge.InMemory"] = Set(Framework, "IwoBridge.Core"),

            ["IwoBridge.Ews"] = Set(Framework, "IwoBridge.Core", "Microsoft.Exchange.WebServices"),

            // Outlook — позднее связывание (dynamic) без Interop-сборок: нужен только связыватель C#.
            ["IwoBridge.Outlook"] = Set(Framework, "IwoBridge.Core", "Microsoft.CSharp"),

            // Приложение с окнами — корень композиции: видит все слои, но EWS Managed API — только через EwsTarget.
            // Сборка называется IwoCalendarBridge (имя exe), проект — IwoBridge.Wpf.
            ["IwoCalendarBridge"] = Set(
                Framework,
                "IwoBridge.Core",
                "IwoBridge.App",
                "IwoBridge.Ews",
                "IwoBridge.InMemory",
                "IwoBridge.Outlook",
                "WindowsBase",
                "PresentationCore",
                "PresentationFramework",
                "System.Xaml"),
        };

        /// <summary>Проект → имя сборки, если они различаются.</summary>
        private static readonly IReadOnlyDictionary<string, string> AssemblyOfProject = new Dictionary<string, string>
        {
            ["IwoBridge.Wpf"] = "IwoCalendarBridge",
        };

        /// <summary>Сборки по одному типу из каждой — так тест получает их без поиска файлов.</summary>
        private static readonly IReadOnlyDictionary<string, Assembly> Assemblies = new[]
        {
            typeof(TimeMapper).Assembly,
            typeof(CleanupRunner).Assembly,
            typeof(InMemoryCalendar).Assembly,
            typeof(EwsCalendarGateway).Assembly,
            typeof(IwoBridge.Outlook.OutlookSession).Assembly,
            typeof(IwoBridge.Wpf.ViewModels.MainViewModel).Assembly,
        }.ToDictionary(a => a.GetName().Name, StringComparer.Ordinal);

        public static IEnumerable<object[]> ProductAssemblies() => Allowed.Keys.Select(name => new object[] { name });

        [Theory]
        [MemberData(nameof(ProductAssemblies))]
        public void References_OnlyAllowed(string assemblyName)
        {
            ISet<string> actual = References(assemblyName);

            // Subset: каждая фактическая ссылка обязана быть в белом списке. Сообщение xUnit перечислит лишние.
            Assert.Subset(Allowed[assemblyName], actual);
        }

        [Fact]
        public void Core_ReferencesNoProductAssembly()
        {
            Assert.DoesNotContain(References("IwoBridge.Core"), r => r.StartsWith(ProductPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public void OnlyEwsAdapter_ReferencesExchange()
        {
            string[] withExchange = Allowed.Keys
                .Where(name => References(name).Any(r => r.StartsWith("Microsoft.Exchange", StringComparison.Ordinal)))
                .ToArray();

            Assert.Equal(new[] { "IwoBridge.Ews" }, withExchange);
        }

        [Fact]
        public void NoAssembly_ReferencesOutlookInterop()
        {
            // Адаптер Outlook работает через позднее связывание: Interop-сборки привязали бы программу к версии и
            // разрядности Office.
            foreach (string name in Allowed.Keys)
            {
                Assert.DoesNotContain(References(name), r =>
                    r.StartsWith("Microsoft.Office", StringComparison.Ordinal)
                    || r.IndexOf("Interop", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        [Fact]
        public void OnlyDesktopApp_ReferencesWpf()
        {
            string[] withWpf = Allowed.Keys
                .Where(name => References(name).Any(r => r == "PresentationFramework" || r == "PresentationCore" || r == "WindowsBase"))
                .ToArray();

            Assert.Equal(new[] { "IwoCalendarBridge" }, withWpf);
        }

        [Fact]
        public void AdaptersDoNotReferenceEachOther()
        {
            Assert.DoesNotContain("IwoBridge.Ews", References("IwoBridge.InMemory"));
            Assert.DoesNotContain("IwoBridge.InMemory", References("IwoBridge.Ews"));
            Assert.DoesNotContain("IwoBridge.App", References("IwoBridge.Ews"));
            Assert.DoesNotContain("IwoBridge.App", References("IwoBridge.InMemory"));
            Assert.DoesNotContain("IwoBridge.Ews", References("IwoBridge.Outlook"));
            Assert.DoesNotContain("IwoBridge.Outlook", References("IwoBridge.Ews"));
            Assert.DoesNotContain("IwoBridge.App", References("IwoBridge.Outlook"));
        }

        [Fact]
        public void EveryProjectInSrc_HasRule()
        {
            string src = Path.Combine(FindRepositoryRoot(), "src");
            // «*_wpftmp.csproj» — временный проект, который сборка WPF создаёт на время компиляции разметки; при
            // параллельной сборке он попадал в список и ронял тест.
            string[] projects = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("_wpftmp.csproj", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .Select(p => AssemblyOfProject.TryGetValue(p, out string? assembly) ? assembly : p)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.NotEmpty(projects);
            Assert.Equal(Allowed.Keys.OrderBy(n => n, StringComparer.Ordinal), projects);
        }

        private static ISet<string> References(string assemblyName)
        {
            Assert.True(Assemblies.ContainsKey(assemblyName), "Нет типа-якоря для сборки " + assemblyName + ".");
            return new HashSet<string>(
                Assemblies[assemblyName].GetReferencedAssemblies().Select(r => r.Name), StringComparer.Ordinal);
        }

        private static ISet<string> Set(IEnumerable<string> baseNames, params string[] extra)
        {
            return new HashSet<string>(baseNames.Concat(extra), StringComparer.Ordinal);
        }

        /// <summary>Корень репозитория — ближайшая вверх папка с IwoBridge.sln (тесты запускаются из bin).</summary>
        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "IwoBridge.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory != null, "Не найден IwoBridge.sln выше папки тестов.");
            return directory!.FullName;
        }
    }
}
