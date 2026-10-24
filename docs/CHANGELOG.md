# Журнал изменений

## W1 — ядро очистки (в работе)

Добавлено ядро `IwoBridge.Core` (net48, без Outlook/EWS/сети):
- модель: `TargetItemInfo`, `ItemKind`;
- время: `TimeMapper` (нормализация к UTC), `TargetTimeZone` (пояс из конфигурации, период, «весь день»);
- конфигурация: `ConnectionProfile` (адрес EWS, ящик, смещение, отпечаток — всё из настроек);
- очистка: `CleanupPlanner` (чистая функция) + `CleanupScope`/`CleanupFilter`/`CleanupPlan`/`SkippedItem`;
- полнота и удаление: `Completeness`, `CompletenessGuard`, `DeleteResult`, `BatchDeleteOutcome`;
- ошибки: `OperationErrorKind`, `ErrorCategory`, `ErrorClassifier`.

Инфраструктура: решение `IwoBridge.sln`, `Directory.Build.props`, `.gitignore`,
`config/connection.example.json` (шаблон с заглушками).

Тесты: `IwoBridge.Core.Tests` — 53 теста, зелёные. Сборка под net48 с `TreatWarningsAsErrors` —
0 предупреждений, 0 ошибок. Данных организации в репозитории нет (проверено сканом токенов).

## W2 (часть 1) — чистая логика адаптера приёмника + исправление ядра

Исправление (ревизия W1): `CleanupPlanner` неверно классифицировал серию, чьё последнее повторение
начинается до окна, но заканчивается внутри него, — считал «полностью снаружи» и при очистке по периоду
оставил бы мусор. Теперь членство учитывает длительность повторения (`SafeAdd`), со смещением в пользу
обрезки. Добавлен регрессионный тест.

Добавлено в `IwoBridge.Core` (без сети, покрыто тестами):
- `Transport`: `Thumbprint` (единая нормализация отпечатка, переиспользована в `ConnectionProfile`),
  `CertificatePinningPolicy` + `CertificateDecision` — закрепление по отпечатку, TOFU при доверенной
  цепочке, детект перехвата HTTPS-антивирусом/прокси (T21), игнор несовпадения имени;
- `Reading`: `TargetPage`, `ITargetPageReader` (единственная граница с сетью при перечислении),
  `TargetSnapshot`, `CalendarSnapshotReader` — постраничный цикл с дедупликацией и проверкой полноты
  (T30, T32), с защитой от зависания (пустая страница и жёсткий потолок страниц).

Тесты: 70 всего (было 53), зелёные. Сборка net48 с `TreatWarningsAsErrors` — 0/0.

## W2 (часть 2) — адаптер EWS

Добавлен проект `IwoBridge.Ews` (net48, Microsoft.Exchange.WebServices 2.2.0):
- транспорт: `ExchangeServiceFactory` (схема Exchange2007_SP1, часовой пояс UTC, TLS 1.0/1.1/1.2,
  NTLM-учётные данные) + `EwsCertificateValidator` поверх `CertificatePinningPolicy`;
- учётные данные: `WindowsCredentialStore` — чтение из диспетчера учётных данных Windows (P/Invoke
  `CredReadW`), реализует порт `ICredentialStore`;
- шлюз `EwsCalendarGateway` — реализует `ITargetPageReader` (постраничное неразвёрнутое перечисление
  основного календаря, времена в UTC) и `ITargetDeleter` (пакетное `DeleteItems` + `SendToNone`);
- чистые мапперы `EwsItemMapper`, `EwsDeleteResultMapper`, `EwsErrorMapper` (покрыты тестами);
- метки моста `BridgeExtendedProperties` (фиксированный GUID-набор продукта).

Ядро пополнено: порты `ICredentialStore`, `ITargetDeleter`; типы `CredentialSecret`, `TargetDeleteMode`;
оркестратор `CleanupExecutor` (пакеты, строгая оценка, отказ по неавторитетному снимку — T2).

Тесты: 88 всего (75 Core + 13 EWS), зелёные. Сетевые классы (транспорт, шлюз, чтение учётных данных)
проверяются интеграционно против тестовой подпапки — отдельным шагом на живом сервере.

## W2 (часть 3) — границы серий и обрезка на границе периода

Ядро:
- `TargetItemInfo` получил `Uid` (iCalendar UID — общий у мастера и повторений).
- `RecurrenceInfo` + порт `ITargetRecurrenceReader` — дочитывание границ повторений и UID (GetItem).
- `RecurrenceEnricher` (чистый) — проставляет мастерам дочитанные границы и UID, не трогая остальное.
- `TargetOccurrence`, `OccurrenceReadResult` + порт `ITargetOccurrenceReader` — раскрытые повторения в окне.
- `SeriesTrimmer` (оркестрация) — удаляет повторения в окне только у заданных серий (по UID), отказывает
  при неавторитетном снимке (T2) и неполном раскрытии окна (T15); удаление — через `CleanupExecutor` (DRY).
- `Batching.Chunk<T>` — единый помощник пакетирования; дублирующий `Chunk` из `CleanupExecutor` убран.

Адаптер EWS (`EwsCalendarGateway`): реализованы `ITargetRecurrenceReader` (`GetItem` границ/UID пакетами)
и `ITargetOccurrenceReader` (`CalendarView` с дроблением окна пополам при усечении, неустранимое усечение
в пределах суток → неполный результат).

Тесты: 97 всего (84 Core + 13 EWS), зелёные; добавлены `RecurrenceEnricherTests` (3) и `SeriesTrimmerTests` (6).
Данных организации нет (скан CLEAN). Сетевые методы шлюза проверяются интеграционно.

## W3 (часть 1) — сценарий очистки, ин-мемори адаптер, исправления W2

Добавлено:
- `IwoBridge.App` — `CleanupRunner` (предпросмотр → подтверждение → свежая сверка → удаление пересечения),
  `CleanupPreview`, `CleanupResult` (в т.ч. число новых и изменившихся с предпросмотра элементов).
- `IwoBridge.InMemory` — `InMemoryCalendar`, `InMemoryEvent`, `InMemoryOccurrence`: реализация портов для
  прогонов и тестов без сервера с поведением EWS на границах.
- Ядро: `TimeWindow`, `OccurrenceSpan`, `CertificateFingerprint`; в `TargetItemInfo` и `RecurrenceInfo` —
  изменённые повторения; в `TargetOccurrence` — конец и признак экземпляра серии; в `CredentialSecret` —
  домен и пользователь; `OperationErrorKind.TlsNegotiationFailed`; новые причины пропуска
  `SeriesUidUnavailable`, `AmbiguousSeriesUid`.

Изменено:
- `CleanupPlanner` — принадлежность периоду по единому правилу, учёт изменённых повторений и экземпляров из
  окна, обрезка только повторений и исключений, защита от неоднозначного UID; план содержит конкретные
  идентификаторы удаляемых экземпляров.
- `CalendarSnapshotReader` — якорный элемент, повтор обхода при сдвиге, устойчивость к урезанным страницам.
- `EwsCalendarGateway` — сериализация вызовов, сортировка по дате создания, `ModifiedOccurrences` и
  `Recurrence` в `GetItem`, `AppointmentType` и конец в `CalendarView`, защитное дробление окна по году.
- `ExchangeServiceFactory` — без изменения процессных настроек TLS, домен для NTLM отдельно.
- `EwsCertificateValidator` — область действия по хосту и порту, потокобезопасность, атомарный TOFU (SHA-256).
- `Thumbprint` — удаление невидимых символов, проверка длины и шестнадцатеричности.
- `WindowsCredentialStore` — точная раскладка `CREDENTIALW`, различение «не найдено» и прочих ошибок.

Удалено: `SeriesTrimmer` (логика в `CleanupPlanner`).

Документация: PLAN §8 переписан под фактическую семантику, ARCHITECTURE — новые схемы и таблицы,
из документов и тестов убраны значения пояса конкретной установки.

## W3 (часть 2) — манифест удаляемого, загрузчик настроек, архитектурные тесты, CI

Добавлено:
- `IwoBridge.App.Cleanup`: `ICleanupManifestWriter`, `CsvCleanupManifestWriter`, `CleanupManifestEntry`;
  `CleanupRunner` пишет манифест до первого удаления (обязательный параметр конструктора), сбой записи →
  отказ без удаления; `CleanupResult.ManifestPath`.
- `IwoBridge.App.Configuration`: `ConnectionProfileFile` (`Load`/`Parse` файла `config/connection.json`),
  `ProfileLoadException` с именем поля.
- Ядро: `TargetItemInfo.Subject`, `TargetOccurrence.Subject`, `TargetItemInfo.WithRecurrence`,
  `CleanupPlan.OccurrencesToDelete`; проверка формы адреса ящика в `ConnectionProfile`.
- EWS: тема в наборах свойств `FindItem` и `CalendarView`.
- Ин-мемори адаптер: темы событий и экземпляров.
- Проект `tests/IwoBridge.Architecture.Tests` — белые списки ссылок сборок.
- `.github/workflows/ci.yml`; `packages.lock.json` во всех проектах (`RestorePackagesWithLockFile`).

Изменено:
- `TargetTimeZone.FromOffset` — параметр `utcOffset` (совпадает с полем настроек).
- Шаблон `config/connection.example.json` — пояснения к обязательным полям и формату.
- Комментарии к тестам и проектам: что проверяет каждый набор.

Тесты: 317 (Core 151, App 118, Ews 39, Architecture 9), зелёные; сборка Release — 0 предупреждений.

## W3 (часть 3) — приложение с окнами

Добавлено:
- Проект `src/IwoBridge.Wpf` (exe `IwoCalendarBridge`): главное окно очистки, окно «Подключение», окно
  подтверждения удаления; модели представления `MainViewModel`, `CleanupViewModel`, `ConnectionViewModel`,
  `DeletionConfirmationViewModel`; тексты ошибок `ErrorMessages`; демонстрационный режим `--demo`.
- Ядро: `ServerAddress` (поле «Сервер» → адрес EWS), `PresentedCertificate`, `SnapshotReadProgress`,
  `CredentialTargets`, `TargetTimeZone.FormatOffset/TryParseOffset/OffsetText`, `BatchDeleteOutcome.Cancelled` и
  `Exception`, `OperationErrorKind.MailboxNotFound`, `ICredentialStore.Save`.
- Сценарий: `CleanupProgress`/`CleanupStage`, ход работы в `PreviewAsync`/`RunAsync`, `CleanupResult.Cancelled` и
  `Exception`, `CleanupLabels`, `CleanupManifestEntry.ListOf`, `ConnectionProfileFile.Save/ToJson`.
- EWS: `EwsTarget` (единая точка входа), `EwsCalendarGateway.CountItemsAsync`, запоминание предъявленного
  сертификата в `EwsCertificateValidator`, `WindowsCredentialStore.Save`.
- Сборки x86 и x64: `build/package.ps1`, шаг CI и артефакт `iwo-Calendar-Bridge`.
- Тесты: `IwoBridge.Wpf.Tests` (59), `ServerAddressTests`, `WindowsCredentialStoreTests` (настоящий диспетчер
  учётных данных под тестовой целью), ход работы и остановка в исполнителе и сценарии, запись профиля.

Изменено:
- `ConnectionProfileLoader` → `ConnectionProfileFile`.
- `CleanupExecutor`: остановка — исход с точным счётом; сбой порта посреди пакета — исход с исключением.
- Документы: совместимость Windows 7+ и .NET Framework 4.8 (вместо 4.8.1), README — установка, подключение,
  очистка; ARCHITECTURE — слой окон.

Тесты: 445 (Core 199, App 126, Ews 50, Wpf 59, Architecture 11), зелёные; Release — 0 предупреждений.

## W3 (часть 4) — аудит всего кода, адаптивные окна, планировщик точечной синхронизации

Исправлено по итогам аудита (четыре независимые проверки: ядро, сценарий, EWS, окна):
- Проверка сертификата: одна на процесс (`TlsTrustRegistry`), выбор проверки — по группе соединений каждого
  подключения (`ConnectionGroupName`). Раньше пробное подключение подменяло проверку действующего сеанса, а
  соединения из пула обходили новое закрепление. `EwsTarget` освобождается и закрывает свою группу.
- Повторы только для временных ошибок (`RetryPolicy`): 1, 2, 4… с, не больше 60 с или подсказка сервера
  `BackOffMilliseconds`; ошибки входа не повторяются никогда.
- Новые коды ошибок EWS: перенос ящика, недоступное хранилище, внутренняя временная ошибка → «сервер занят»;
  ErrorTimeoutExpired, обрыв соединения, 404 (`EndpointNotFound`), 403 и ErrorFolderNotFound → права;
  превышение лимита поиска → окно чтения делится.
- Удаление: `AffectedTaskOccurrence.AllOccurrences`, уже удалённый экземпляр серии — «уже нет»; результат без
  статуса (`DeleteItemStatus.Unknown`) не засчитывается; исключение порта посреди пакета — исход с точным счётом.
- Повреждённые элементы (не читаются начало, конец или вид) не удаляются, а показываются в «Останется».
- Планировщик очистки: неизвестный фильтр — ошибка; граница серии — по первому и последнему повторению; серия с
  известным UID обрезается только при экземплярах в периоде; общий UID с удаляемой серией — «неоднозначно».
- Адрес сервера: отклоняются имя и пароль в адресе, устаревшая запись IPv4 (`010.0.0.5`, `10.5`), невидимые
  символы, порт 0, фрагмент, `https:/host`. Учётная запись и ящик — без пустых частей и невидимых символов.
- Пояс: даты периода ограничены 1601–4500 годами (раньше 31.12.9999 давало переполнение).
- Окно «Подключение»: закрепление сертификата следует за адресом сервера; пароль сохраняется до профиля; после
  закрытия окна ничего не сохраняется. Закрытие главного окна во время удаления — без гонки. Прежний сеанс
  освобождается при смене подключения.
- Запись файлов: замена через `File.Replace` с восстановлением при ошибке переноса (`FileSafety`).

Добавлено:
- Адаптивная страница главного окна (`FillPanel`): таблица занимает свободное место, в маленьком окне
  прокручивается страница, таблица сохраняет виртуализацию строк. Окна не больше экрана (`WindowSizing`),
  минимальный размер главного окна 720×480; строки дат и вариантов удаления переносятся.
- Высокая контрастность Windows (`Theme`): цвета заменяются системными на лету.
- Озвучивание сообщений экранным диктором (`Announce`, LiveRegionChanged).
- Клавиши F5 (предпросмотр) и Esc (остановить); Alt-буквы не повторяются; Enter строит предпросмотр, только
  пока его нет; подсказки ко всем кнопкам и вариантам; длинные темы обрезаются многоточием с подсказкой.
- Окно подтверждения: «Адрес совпадает» с галочкой; на маленьком экране текст прокручивается, кнопки на месте.
- Журнал непредвиденных ошибок `logs\crash.log` (`CrashLog`, не больше 1 МБ + одна старая копия).
- Ядро `Core.Sync`: `SyncContent` (отпечаток содержимого), `SyncPlanner` — точечная синхронизация за период
  (создать / перезаписать / пересоздать / удалить, защита от массового удаления). Адаптеры Outlook и записи в
  EWS — следующий этап.
- Тесты окон на настоящей разметке (`UiLayoutTests`): 4 размера окна, ошибки привязок, виртуализация,
  достижимость кнопок, Alt-буквы, палитра высокой контрастности.

Тесты: 554 (Core 266, App 126, Ews 66, Wpf 85, Architecture 11), зелёные.

## W3 (часть 5) — резервный адрес сервера

Добавлено:
- Ядро: `ServerEndpoint`, `ConnectionProfile.AlternateEwsUrl`, `AlternatePinnedCertificateThumbprint`,
  `Endpoints`, `ForEndpoint`; `ErrorClassifier.IsUnreachable` — единое правило перехода на другой адрес.
- EWS: `EwsCalendarGateway` с несколькими адресами — недоступный адрес → тот же запрос по следующему, дальше
  работа через него; `ActiveEndpointIndex`. `EwsTarget` — своя группа соединений и проверка сертификата у
  каждого адреса, `UsesAlternate`.
- Файл настроек: поля `alternateEwsUrl`, `alternatePinnedCertificateThumbprint` (необязательные), шаблон
  `config/connection.example.json`.
- Окно «Подключение»: поле «Резервный сервер» (Alt+З), проверка каждого адреса, доверие к сертификату — у каждого
  адреса своё; в шапке главного окна виден резервный адрес.
- Тесты: переход на настоящем стеке EWS Managed API и HTTP (закрытый порт и местный поддельный EWS), правила
  профиля и файла, окно «Подключение» с двумя адресами.

Тесты: 591 (Core 278, App 137, Ews 70, Wpf 95, Architecture 11), зелёные.
