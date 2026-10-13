using System.Text;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Запись файла через временный: если OBS убьют посреди записи, останется целый старый файл, а не обрезанный новый.
    /// Для конфига это важно: обрезанный config.json не прочитался бы, и все фильтры пропали бы.
    /// </summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string text, Encoding? encoding = null)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, text, encoding ?? new UTF8Encoding(false));
                MoveWithRetry(temp, path);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        // Антивирус или программа резервного копирования могут на доли секунды удерживать файл: пробуем ещё пару раз
        private static void MoveWithRetry(string from, string to)
        {
            const int attempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(from, to, overwrite: true);
                    return;
                }
                // занятый файл Windows отдаёт как IOException или как «отказано в доступе»
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
    }
}
