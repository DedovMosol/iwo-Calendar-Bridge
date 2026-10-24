# Архитектура

<div align="justify">

Документ описывает устройство кода: слои, порты, адаптеры, потоки данных и основные решения. Схемы приведены
на языке Mermaid и отображаются на GitHub.

## Принципы

- **Порты и адаптеры.** Ядро не зависит от внешних систем. EWS, Outlook и календарь в памяти подключаются
  через порты, зависимости направлены внутрь, к ядру.
- **Разделение ответственности.** `IwoBridge.Core` не ссылается на EWS, COM и WPF. Время нормализуется в
  `TimeMapper`, пояс и границы периода считает `TargetTimeZone`, попадание в период определяет `TimeWindow`.
- **Одно правило — одна реализация.** Попадание в период, полнота ответов, оценка пакетного удаления,
  классификация ошибок, отпечаток содержимого, разбиение на пакеты.
- **Простота.** Планирование очистки и синхронизации — чистые функции от прочитанных данных. Порты появляются
  вместе с адаптерами, которые их реализуют.
- **Сначала чтение, затем запись.** Изменения выполняются по полностью прочитанным данным и в объёме,
  который подтвердил пользователь или правило автокопирования.

## 1. Контекстная схема

```mermaid
flowchart LR
  subgraph PC["Компьютер пользователя"]
    OL["Outlook — любые календари профиля и «Общие календари»"]
    APP["iwo Calendar Bridge — окно и область уведомлений"]
    CM["Credential Manager — DPAPI"]
    CFG["config/*.json"]
  end
  SRC[("Exchange: календарь-источник по EWS")]
  DST[("Exchange: календарь-приёмник")]
  PH["Мобильные клиенты"]

  OL -->|COM, только чтение| APP
  SRC -->|EWS, только чтение| APP
  CM -->|пароли| APP
  CFG -->|адреса, ящики, пояс, отпечатки, настройки копирования| APP
  APP -->|EWS, NTLM, TLS| DST
  DST -->|ActiveSync| PH
```

Копии пишутся в основной календарь ящика-приёмника, его видят Outlook, OWA и мобильные клиенты.

## 2. Слои и порты

```mermaid
flowchart TB
  GUI["IwoBridge.Wpf — окна, модели представления, расписание, область уведомлений (корень композиции)"]
  APPL["IwoBridge.App — CopyRunner, CleanupRunner, манифест, файлы настроек"]
  FILES[("config/*.json · manifests/*.csv")]

  subgraph CORE["IwoBridge.Core — логика без ввода-вывода"]
    PORTS(["Порты: ISourceCalendar · ICopyTarget · ITargetPageReader · ITargetRecurrenceReader · ITargetOccurrenceReader · ITargetDeleter · ICredentialStore"])
    LOGIC["SyncPlanner · OwnEventMatcher · CopyRules · CleanupPlanner · CleanupExecutor · CalendarSnapshotReader · ErrorClassifier · RetryPolicy · TargetTimeZone · CertificatePinningPolicy"]
  end

  EWS["IwoBridge.Ews — EwsTarget, EwsChannel, EwsSource, EwsCopyTarget, EwsCalendarGateway"]
  OUT["IwoBridge.Outlook — OutlookSession, OutlookSource, StaWorker"]
  MEM["IwoBridge.InMemory — календари в памяти для демонстрации и тестов"]

  GUI --> APPL
  GUI --> EWS
  GUI --> OUT
  GUI --> MEM
  APPL --> LOGIC
  APPL --> PORTS
  APPL --> FILES
  EWS -.->|реализует| PORTS
  OUT -.->|реализует| PORTS
  MEM -.->|реализует| PORTS
```

Архитектурные тесты держат эту схему: у каждой сборки белый список ссылок. Ядро ссылается только на базовые
сборки .NET Framework, EWS Managed API разрешён только `IwoBridge.Ews`, позднее связывание COM — только
`IwoBridge.Outlook`, WPF и WinForms — только приложению, адаптеры не ссылаются друг на друга.

## 3. Ядро

| Область | Типы | Назначение |
|---|---|---|
| Time | `TimeMapper`, `TargetTimeZone`, `TimeWindow` | Нормализация к UTC, пояс календаря из профиля, границы периода, правило попадания в период |
| Model | `TargetItemInfo`, `TargetOccurrence`, `OccurrenceSpan`, `ItemKind` | Неизменяемые описания элементов приёмника и экземпляров серий, время в UTC |
| Configuration | `ConnectionProfile`, `ServerEndpoint`, `ServerAddress` | Адрес EWS и резервный адрес (у каждого свой отпечаток), ящик, смещение пояса |
| Transport | `Thumbprint`, `CertificateFingerprint`, `CertificatePinningPolicy` | Нормализация и сравнение отпечатков SHA-1 и SHA-256, первое закрепление, распознавание HTTPS-инспекции |
| Security | `CredentialSecret`, `ICredentialStore` | Учётные данные (домен отдельно для NTLM) и порт их хранилища |
| Errors | `OperationErrorKind`, `ErrorClassifier`, `RetryPolicy` | Классификация, повторы временных ошибок с паузами 1, 2, 4… с (до 60 с или по подсказке сервера), признаки «остановить пакет» и «нужен человек» |
| Reading, Paging | `CalendarSnapshotReader`, `CompletenessGuard`, `BatchDeleteOutcome` | Постраничный обход с якорем и повтором при сдвиге, строгая проверка полноты чтения и удаления |
| Cleanup | `CleanupPlanner`, `CleanupExecutor`, `CleanupPlan` | Планирование очистки и пакетное удаление |
| Sync | `SourceItem`, `RecurrenceRule`, `SyncContent`, `MapiRtf`, `CopyRules`, `SyncPlanner`, `OwnEventMatcher`, `ISourceCalendar`, `ICopyTarget` | Модель события источника, отпечаток содержимого, правила для личных событий, план синхронизации, сверка с событиями пользователя |

Логика ядра — чистые функции и неизменяемые типы. Изменяемое состояние есть только в адаптерах и приложении,
и доступ к нему упорядочен (семафор канала EWS, поток STA Outlook, поток интерфейса).

## 4. Копирование

```mermaid
sequenceDiagram
  actor U as Пользователь или расписание
  participant VM as CopyViewModel
  participant Run as CopyRunner
  participant Dst as ICopyTarget (EwsCopyTarget)
  participant Src as ISourceCalendar (Outlook или EWS)
  participant Plan as SyncPlanner

  U->>VM: предпросмотр (F5) или запуск по расписанию
  VM->>Run: PreviewAsync(период)
  Run->>Dst: ReadCopiesAsync — копии с метками
  Run->>Src: ReadAsync — события периода и события, на которые ссылаются копии
  Run->>Run: CopyRules — правило для личных событий
  Run->>Plan: Plan(источник, копии, пороги)
  Plan-->>Run: Create, Update, Recreate, Delete
  Run->>Dst: ReadOwnEventsAsync — события пользователя (если есть что создавать)
  Run-->>VM: план, рекомендованные действия, замечания
  U->>VM: выбор строк и подтверждение (вручную)
  VM->>Run: RunAsync(выбранные действия)
  loop по одному действию
    Run->>Src: LoadAttachmentsAsync (если есть вложения)
    Run->>Dst: Create / Update / Delete
  end
  Run-->>VM: итог и сбои по событиям
```

- **Сопоставление.** Копия хранит `IwoBridgeSourceId` и `IwoBridgeContentHash` (extended properties). Если
  отпечаток изменился, копия перезаписывается. Серия при изменении пересоздаётся. Копии, которых больше нет в
  источнике, удаляются.
- **События пользователя.** `OwnEventMatcher` сравнивает новые события с событиями приёмника без меток (вид,
  начало, конец, тема). Совпадения не входят в рекомендованные действия.
- **Пороги.** План с большим числом удалений выполняется только после явного подтверждения. Если источник
  прочитан не полностью, план строится без удалений.
- **Запись в EWS.** Событие, вложения и исключения серии записываются как одно целое. При сбое созданная
  копия удаляется, при перезаписи отпечаток пишется последним запросом. Время пишется в поясе календаря
  приёмника (`CalendarZones`, классические ID зон Windows), описание — исходным RTF (`MapiRtf`).
- **Остановка.** «Остановить» прерывает чтение сразу, запись — после текущего события.

## 5. Автокопирование

```mermaid
stateDiagram-v2
  [*] --> Ожидание: включено
  Ожидание --> Запуск: таймер
  Запуск --> Ожидание: занято другим — через минуту
  Запуск --> Копирование: свободно
  Копирование --> Ожидание: готово — через интервал
  Копирование --> Пауза: вход, права, сертификат
  Пауза --> Ожидание: подключение изменено
  Ожидание --> [*]: выключено
```

`AutoCopyViewModel` ведёт расписание на однократном `DispatcherTimer`: следующий запуск назначается после
окончания текущего. Сам запуск выполняет `CopyViewModel.RunScheduledAsync`. Он ждёт, если идёт другая
операция или открыт модальный диалог, перечитывает список календарей Outlook и сдвигает период от текущей
даты. Календарь Outlook выбирается по EntryID папки. `TrayIcon` показывает значок, меню и уведомления о
проблемах. `RegistryAutostart` пишет запуск с ключом `--tray` в `HKCU\…\Run`.

## 6. Очистка

```mermaid
sequenceDiagram
  actor U as Пользователь
  participant VM as CleanupViewModel
  participant Run as CleanupRunner
  participant Snap as CalendarSnapshotReader
  participant Ews as EwsCalendarGateway
  participant Plan as CleanupPlanner
  participant Man as CsvCleanupManifestWriter

  U->>VM: объём и период
  VM->>Run: PreviewAsync
  loop страницы FindItem с якорем
    Snap->>Ews: ReadPageAsync
  end
  opt период и серии
    Run->>Ews: данные серий (GetItem) и экземпляры окна (CalendarView)
  end
  Run->>Plan: BuildPlan
  Run-->>VM: «Будет удалено» и «Останется»
  U->>VM: подтверждение вводом адреса ящика
  VM->>Run: RunAsync
  Run->>Run: свежий снимок и план, удаляется пересечение с подтверждённым
  Run->>Man: манифест до первого удаления
  loop пакеты по 50
    Run->>Ews: DeleteItems, SendToNone
  end
  Run-->>VM: итог
```

Серия целиком в периоде удаляется мастером. Серия на границе периода обрезается удалением экземпляров внутри
периода (по UID, с учётом перенесённых исключений). Неполное чтение на любом шаге оставляет календарь без
изменений.

## 7. Проверка сертификата

```mermaid
flowchart TB
  A["Соединение"] --> H{"Группа соединений подключения EWS?"}
  H -->|нет| STD["Стандартная проверка Windows"]
  H -->|да| B{"Отпечаток закреплён?"}
  B -->|да| C{"Совпал SHA-1 или SHA-256?"}
  C -->|да| OK1["Принять"]
  C -->|нет| R2["Отклонить: другой сертификат"]
  B -->|нет| E{"Издатель — HTTPS-инспекция?"}
  E -->|да| R1["Отклонить: перехват"]
  E -->|нет| F{"Цепочка доверенная?"}
  F -->|да| OK2["Принять и закрепить SHA-256"]
  F -->|нет| R3["Показать сертификат пользователю"]
```

`TlsTrustRegistry` регистрирует одну проверку на процесс и выбирает правило по группе соединений запроса. У
каждого адреса каждого подключения своя группа и своё закрепление. Несовпадение имени в сертификате с адресом
допускается, так как к серверу часто подключаются по IP.

## 8. Адаптеры

- **EWS (`IwoBridge.Ews`).** `EwsTarget` — единственная точка входа приложения. Он создаёт сервисы для каждого
  адреса, канал чтения в UTC и канал записи в поясе календаря. `EwsChannel` выполняет запросы по одному
  (семафор), переходит на резервный адрес при сетевой недоступности и повторяет временные ошибки по
  `RetryPolicy`. Создание копии не повторяется автоматически. Схема запросов — `Exchange2007_SP1`, она
  поддерживается серверами 2007 SP1 … 2013. Протокол TLS выбирает Windows.
- **Outlook (`IwoBridge.Outlook`).** Позднее связывание COM, все вызовы в одном потоке STA (`StaWorker`) с
  фильтром сообщений. Вызов ограничен по времени, зависший поток заменяется новым. После перезапуска Outlook
  подключение восстанавливается. Таблица папки (`GetTable`) даёт быстрый перечень, затем открываются только
  отобранные события. Каждый объект COM освобождается сразу (`Com.Release`).
- **В памяти (`IwoBridge.InMemory`).** Реализует те же порты и воспроизводит поведение EWS на границах:
  перечисление без данных серий, надмножество CalendarView, удаление мастера и экземпляра, отсутствующий id.

## 9. Приложение с окнами

Exe `IwoCalendarBridge`, .NET Framework 4.8, Windows 7 SP1+, x86 и x64. Корень композиции — `App`, только
там создаются реальные реализации портов. Модели представления не используют типы WPF и тестируются без окна.

| Слой | Типы | Назначение |
|---|---|---|
| Модели представления | `MainViewModel`, `CopyViewModel`, `AutoCopyViewModel`, `CleanupViewModel`, `ConnectionViewModel`, `DeletionConfirmationViewModel` | Вкладки, подключения, копирование, расписание, очистка, подтверждения |
| Службы | `ITargetSession`, `ITargetConnector`, `ISettingsStore`, `IMailboxStores`, `ICopySettingsStore`, `IOutlookCalendars`, `IOneShotTimer`, `IAutostart`, `IDialogService` | Сеть, файлы, Outlook, таймер, реестр и окна за портами |
| Окна | `MainWindow`, `CopyPage`, `ConnectionWindow`, `ConfirmDeletionWindow`, `QuestionWindow`, `HelpWindow`, `TrayIcon` | Разметка, в коде окон только то, что требует WPF |
| Вид | `FillPanel`, `WindowSizing`, `Theme`, `Announce` | Адаптивная страница, окна в пределах экрана, высокая контрастность, озвучивание |
| Тексты | `ErrorMessages`, `UserMessage`, `RussianText` | «Что случилось, что делать, подробности» для каждого вида ошибки, числа, даты, склонения |

## 10. Тестирование

- **Ядро и сценарии** — на синтетических данных и календарях в памяти.
- **EWS** — мапперы, ошибки, правила повторения, проверка сертификата на сгенерированных сертификатах,
  переход на резервный адрес на реальном HTTP-стеке.
- **Outlook** — маппинг правил повторения.
- **Окна** — модели представления без окна, расписание на подставном таймере, разметка в настоящих окнах вне
  экрана на нескольких размерах (ошибки привязок, достижимость кнопок, виртуализация, Alt-клавиши, видимость
  для UI Automation).
- **Live-тесты** — по переменным окружения `IWO_LIVE_*`, запись только во временную папку календаря.
- **CI** — восстановление пакетов по lock-файлам, сборка Release, тесты, упаковка x86 и x64.

</div>
