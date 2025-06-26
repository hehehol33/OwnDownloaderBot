namespace TikTok_bot
{
    public class VideoClient
    {
        private readonly string _downloadUrl;
        private readonly string _deleteUrl;
        private readonly HttpClient _httpClient;

        public VideoClient(string downloadUrl, string deleteUrl)
        {
            _downloadUrl = downloadUrl;
            _deleteUrl = deleteUrl;
            _httpClient = new HttpClient();
        }

        public async Task<Stream> DownloadFileAsStreamAsync()
        {
            HttpClient client = new HttpClient();

            HttpResponseMessage response = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            Stream stream = await response.Content.ReadAsStreamAsync();
            return stream; // тяжело нужно будет очистить после использования, иначе утечка памяти будет
        }
        /// <summary>
        /// Получает видео по HTTP и возвращает поток.
        /// </summary>
        /// <summary>
        /// Скачивает видео и сохраняет во временный файл, возвращает путь к нему.
        /// </summary>
        /// тоже на будущее для тг апишки 
        public async Task<string> DownloadToFileAsync(string fileName = "video.mp4")
        {
            var response = await _httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            string tempPath = Path.Combine(Path.GetTempPath(), fileName);

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = File.Create(tempPath))
            {
                await stream.CopyToAsync(fileStream);
            }

            return tempPath;
        }
        /// <summary>
        /// Удаляет файл по указанному пути. Возвращает true при успехе, false при неудаче.
        /// </summary>
        /// на будущее когда подлючим  телеграм апи
        public static bool DeleteFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] -  Путь не задан.");
                    return false;
                }

                if (!File.Exists(path))
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] - Файл не существует");
                    return false;
                }

                File.Delete(path);

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] - Файл успешно удалён");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] - Доступ запрещён при удалении файла: {ex.Message}");
                return false;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] - Ошибка ввода-вывода при удалении: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] - Непредвиденная ошибка: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// Отправляет HTTP DELETE запрос для удаления видео.
        /// </summary>
        public async Task SendDeleteRequestAsync()
        {
            var response = await _httpClient.DeleteAsync(_deleteUrl);
             response.EnsureSuccessStatusCode();
           
        }
    }
}
