using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Тексты вкладки копирования: строки предпросмотра, сводка плана, вопрос перед копированием, итог. Чистые функции.</summary>
    internal static class CopyTexts
    {
        /// <summary>
        /// Замечания к записанным копиям для итога: «\n\nЗаписано с замечаниями (2):\n• «Тема»: …» (не больше 20 строк);
        /// замечаний нет — пустая строка.
        /// </summary>
        internal static string DescribeRemarks(CopyResult result)
        {
            if (result.Remarks.Count == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder("\n\nЗаписано с замечаниями (").Append(RussianText.Number(result.Remarks.Count)).Append("):");
            foreach (string remark in result.Remarks.Take(20))
            {
                text.Append("\n• ").Append(remark);
            }

            if (result.Remarks.Count > 20)
            {
                text.Append("\n• …и ещё ").Append(RussianText.Number(result.Remarks.Count - 20));
            }

            return text.ToString();
        }

        internal static string DescribeDone(CopyResult result)
        {
            if (result.Done == 0)
            {
                return "Ничего не записано.";
            }

            var parts = new List<string>();
            if (result.Created > 0)
            {
                parts.Add("создано " + RussianText.Count(result.Created, "событие", "события", "событий"));
            }

            if (result.Updated > 0)
            {
                parts.Add("перезаписано " + RussianText.Number(result.Updated));
            }

            if (result.Deleted > 0)
            {
                parts.Add("удалено копий " + RussianText.Number(result.Deleted));
            }

            string line = string.Join(", ", parts) + ".";
            return char.ToUpper(line[0], RussianText.Culture) + line.Substring(1);
        }

        internal static string Describe(SyncPlan plan)
        {
            if (plan.IsEmpty)
            {
                return plan.UnchangedCount == 0
                    ? "Копировать нечего"
                    : "Всё уже скопировано: " + RussianText.Count(plan.UnchangedCount, "событие совпадает", "события совпадают", "событий совпадают");
            }

            var parts = new List<string>();
            int create = plan.Count(SyncActionKind.Create);
            int update = plan.Count(SyncActionKind.Update) + plan.Count(SyncActionKind.Recreate);
            int delete = plan.Count(SyncActionKind.Delete);
            if (create > 0)
            {
                parts.Add("создать " + RussianText.Number(create));
            }

            if (update > 0)
            {
                parts.Add("перезаписать " + RussianText.Number(update));
            }

            if (delete > 0)
            {
                parts.Add("удалить копий " + RussianText.Number(delete));
            }

            if (plan.UnchangedCount > 0)
            {
                parts.Add("без изменений " + RussianText.Number(plan.UnchangedCount));
            }

            return string.Join(", ", parts);
        }

        internal static string ConfirmationText(IReadOnlyCollection<SyncAction> selected, string source, string target)
        {
            int create = selected.Count(a => a.Kind == SyncActionKind.Create);
            int update = selected.Count(a => a.Kind == SyncActionKind.Update || a.Kind == SyncActionKind.Recreate);
            int delete = selected.Count(a => a.Kind == SyncActionKind.Delete);
            var text = new StringBuilder();
            text.Append("Откуда: ").Append(source).Append("\nКуда: ").Append(target).Append("\n");

            // Только то, что будет сделано: строки с нулём не показываются.
            if (create > 0)
            {
                text.Append("\n• создать ").Append(RussianText.Count(create, "событие", "события", "событий"));
            }

            if (update > 0)
            {
                text.Append("\n• перезаписать ").Append(RussianText.Count(update, "копию", "копии", "копий"));
            }

            if (delete > 0)
            {
                text.Append("\n• удалить ").Append(RussianText.Count(delete, "копию", "копии", "копий"))
                    .Append(" — их событий больше нет в источнике (в папку «Удалённые», можно вернуть)");
            }

            text.Append("\n\nИсточник не меняется, ваши собственные события не трогаются, участникам ничего не рассылается.");
            return text.ToString();
        }

        internal static IReadOnlyList<CopyRow> BuildRows(CopyPreview preview, TargetTimeZone zone)
        {
            return preview.Plan.Actions.Select(action =>
            {
                SourceItem? item = action.Source != null && preview.SourceItems.TryGetValue(action.SourceId, out SourceItem? found) ? found : null;
                string subject = item?.Subject ?? action.Copy?.Subject ?? "(без темы)";
                DateTime start = item?.StartUtc ?? action.Copy!.SpanStartUtc;
                string when = RussianText.Moment(start, zone) + (item?.Recurrence != null || action.Copy?.Kind == Core.Model.ItemKind.RecurringMaster ? " (серия)" : string.Empty);
                // Такое же событие человек уже завёл сам — копия по умолчанию не создаётся (иначе дубль), но её можно отметить.
                bool duplicate = preview.IsDuplicateOfOwn(action);
                return new CopyRow(
                    action,
                    KindLabel(action.Kind),
                    string.IsNullOrEmpty(subject) ? "(без темы)" : subject,
                    when,
                    duplicate ? "У вас уже есть такое же событие" : ReasonLabel(action.Reason))
                {
                    IsSelected = !duplicate,
                };
            }).ToArray();
        }

        internal static string KindLabel(SyncActionKind kind)
        {
            switch (kind)
            {
                case SyncActionKind.Create:
                    return "Создать";
                case SyncActionKind.Update:
                    return "Перезаписать";
                case SyncActionKind.Recreate:
                    return "Пересоздать";
                default:
                    return "Удалить копию";
            }
        }

        internal static string ReasonLabel(SyncReason reason)
        {
            switch (reason)
            {
                case SyncReason.NewInSource:
                    return "Новое в источнике";
                case SyncReason.ChangedInSource:
                    return "Изменилось в источнике";
                case SyncReason.UnknownCopyVersion:
                    return "Копия старой версии программы";
                case SyncReason.KindChanged:
                    return "Стало серией или перестало ею быть";
                case SyncReason.DeletedInSource:
                    return "Удалено в источнике";
                default:
                    return "Лишняя копия того же события";
            }
        }
    }
}
