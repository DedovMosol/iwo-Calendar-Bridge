using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Text;

namespace IwoBridge.App.History
{
    /// <summary>
    /// Журнал операций в файле <c>logs\history.jsonl</c> рядом с программой — формат JSON Lines: одна операция — одна
    /// строка JSON (UTF-8 без BOM), время — ISO 8601 в UTC. Строку дописывают в конец, поэтому оборванная запись (выключили
    /// питание) портит только её саму: при чтении такие строки пропускаются, а следующая запись начинается с новой строки.
    ///
    /// Размер ограничен: файл больше <see cref="MaxBytes"/> переименовывается в <c>history.1.jsonl</c> (прежний такой
    /// удаляется), и журнал начинается заново — на диске не больше двух файлов. Читаются оба.
    /// </summary>
    public sealed class JsonLinesOperationLog : IOperationLog
    {
        /// <summary>Порог переноса в архивный файл: около двух-трёх тысяч операций.</summary>
        public const long MaxBytes = 1024 * 1024;

        private const int SchemaVersion = 1;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Один сериализатор на все строки: построение контракта дорого, а журнал читается тысячами строк. Экземпляр без
        // суррогатов и резолверов потокобезопасен.
        private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(Line));
        private readonly string _path;
        private readonly string _archive;
        private readonly long _maxBytes;
        private readonly object _sync = new object();

        public JsonLinesOperationLog(string path, long maxBytes = MaxBytes)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
            _archive = Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileNameWithoutExtension(_path) + ".1" + Path.GetExtension(_path));
            _maxBytes = maxBytes > 0 ? maxBytes : MaxBytes;
        }

        public event EventHandler<OperationRecord>? Recorded;

        public string? Location => _path;

        public void Record(OperationRecord record)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            byte[] line = Utf8NoBom.GetBytes(Serialize(record) + "\n");
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    TryRotate(line.Length);
                    using (var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        // Прошлая запись оборвалась (выключили питание — хвост без перевода строки или из нулей): новая
                        // начинается с новой строки, иначе слилась бы с оборванной и пропала вместе с ней.
                        bool torn = stream.Length > 0 && EndsWithoutNewLine(stream);
                        stream.Seek(0, SeekOrigin.End);
                        if (torn)
                        {
                            stream.WriteByte((byte)'\n');
                        }

                        stream.Write(line, 0, line.Length);
                    }
                }
                catch (Exception ex) when (IsFileProblem(ex))
                {
                    // Журнал — справка, а не часть операции: она уже выполнена и показана в окне.
                    Trace.TraceWarning("Журнал операций не записан: " + ex.Message);
                }
            }

            Recorded?.Invoke(this, record);
        }

        /// <summary>Сообщения о ходе работы в файл не пишутся (журнал — итоги операций).</summary>
        public void Info(string message)
        {
        }

        public IReadOnlyList<OperationRecord> Recent(int max)
        {
            if (max <= 0)
            {
                return Array.Empty<OperationRecord>();
            }

            var all = new List<OperationRecord>();
            lock (_sync)
            {
                ReadInto(_archive, all);
                ReadInto(_path, all);
            }

            return all.Skip(Math.Max(0, all.Count - max)).Reverse().ToArray();
        }

        /// <summary>
        /// Перенос полного файла в архив. Не вышло (файл открыт в другой программе без общего доступа) — запись идёт в
        /// прежний файл, перенос повторится со следующей записью: операция в журнале важнее его размера.
        /// </summary>
        private void TryRotate(int adding)
        {
            try
            {
                var file = new FileInfo(_path);
                if (!file.Exists || file.Length + adding <= _maxBytes)
                {
                    return;
                }

                if (File.Exists(_archive))
                {
                    File.Delete(_archive);
                }

                File.Move(_path, _archive);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                Trace.TraceWarning("Журнал операций не перенесён в архив: " + ex.Message);
            }
        }

        private static bool EndsWithoutNewLine(FileStream stream)
        {
            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() != '\n';
        }

        private static void ReadInto(string path, List<OperationRecord> target)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        OperationRecord? record = Parse(line);
                        if (record != null)
                        {
                            target.Add(record);
                        }
                    }
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                Trace.TraceWarning("Журнал операций не прочитан: " + ex.Message);
            }
        }

        private static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is NotSupportedException;

        internal static string Serialize(OperationRecord record)
        {
            var document = new Line
            {
                Version = SchemaVersion,
                Kind = KindCode(record.Kind),
                Started = record.StartedUtc.ToString("o", CultureInfo.InvariantCulture),
                Finished = record.FinishedUtc.ToString("o", CultureInfo.InvariantCulture),
                Outcome = OutcomeCode(record.Outcome),
                Source = record.Source,
                Target = record.Target,
                Period = record.Period,
                Created = record.Created,
                Updated = record.Updated,
                Deleted = record.Deleted,
                Failed = record.Failed,
                Title = record.Title,
                Details = record.Details.ToArray(),
            };
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, document);
                return Utf8NoBom.GetString(stream.ToArray());
            }
        }

        /// <summary>Строка журнала → запись; повреждённая или чужая строка — <c>null</c> (пропускается).</summary>
        internal static OperationRecord? Parse(string line)
        {
            string text = line.Trim();
            if (text.Length == 0 || text[0] != '{')
            {
                return null;
            }

            try
            {
                Line? document;
                using (var stream = new MemoryStream(Utf8NoBom.GetBytes(text), writable: false))
                {
                    document = Serializer.ReadObject(stream) as Line;
                }

                if (document is null
                    || !DateTime.TryParse(document.Started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime started)
                    || !DateTime.TryParse(document.Finished, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime finished))
                {
                    return null;
                }

                return new OperationRecord(
                    ParseKind(document.Kind),
                    started.ToUniversalTime(),
                    finished.ToUniversalTime(),
                    ParseOutcome(document.Outcome),
                    document.Source,
                    document.Target ?? string.Empty,
                    document.Period ?? string.Empty,
                    document.Created,
                    document.Updated,
                    document.Deleted,
                    document.Failed,
                    document.Title ?? string.Empty,
                    document.Details);
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException || ex is ArgumentException || ex is FormatException)
            {
                return null;
            }
        }

        private static string KindCode(OperationKind kind) =>
            kind == OperationKind.AutoCopy ? "auto-copy" : kind == OperationKind.Cleanup ? "cleanup" : "copy";

        private static OperationKind ParseKind(string? code) =>
            code == "auto-copy" ? OperationKind.AutoCopy : code == "cleanup" ? OperationKind.Cleanup : OperationKind.Copy;

        private static string OutcomeCode(OperationOutcome outcome) =>
            outcome == OperationOutcome.Success ? "success" : outcome == OperationOutcome.Cancelled ? "cancelled" : "problem";

        private static OperationOutcome ParseOutcome(string? code) =>
            code == "success" ? OperationOutcome.Success : code == "cancelled" ? OperationOutcome.Cancelled : OperationOutcome.Problem;

        [DataContract]
        private sealed class Line
        {
            [DataMember(Name = "v", Order = 0)]
            public int Version { get; set; }

            [DataMember(Name = "kind", Order = 1)]
            public string? Kind { get; set; }

            [DataMember(Name = "started", Order = 2)]
            public string? Started { get; set; }

            [DataMember(Name = "finished", Order = 3)]
            public string? Finished { get; set; }

            [DataMember(Name = "outcome", Order = 4)]
            public string? Outcome { get; set; }

            [DataMember(Name = "source", Order = 5, EmitDefaultValue = false)]
            public string? Source { get; set; }

            [DataMember(Name = "target", Order = 6)]
            public string? Target { get; set; }

            [DataMember(Name = "period", Order = 7)]
            public string? Period { get; set; }

            [DataMember(Name = "created", Order = 8)]
            public int Created { get; set; }

            [DataMember(Name = "updated", Order = 9)]
            public int Updated { get; set; }

            [DataMember(Name = "deleted", Order = 10)]
            public int Deleted { get; set; }

            [DataMember(Name = "failed", Order = 11)]
            public int Failed { get; set; }

            [DataMember(Name = "title", Order = 12)]
            public string? Title { get; set; }

            [DataMember(Name = "details", Order = 13)]
            public string[]? Details { get; set; }
        }
    }
}
