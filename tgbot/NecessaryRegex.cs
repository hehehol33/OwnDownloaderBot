using System.Text.RegularExpressions;

namespace TikTok_bot
{
    /// <summary>
    /// Класс для извлечения ссылок на платформы TikTok, Instagram и YouTube из текста с использованием регулярных выражений.
    /// </summary>
    internal class NecessaryRegex
    {
        /// <summary>
        /// Извлекает ссылку на TikTok из переданного текста.
        /// </summary>
        /// <param name="text">Текст, в котором ищется ссылка на TikTok.</param>
        /// <returns>Возвращает найденную ссылку TikTok или пустую строку, если ссылка не найдена.</returns>
        public static string ExtractTikTokUrl(string text)
        {
            Regex regex = new(@"https?:\/\/(www\.)?(vt\.tiktok\.com\/[\w\-]+\/?|vm\.tiktok\.com\/[\w\-]+\/?|tiktok\.com\/@[A-Za-z0-9_.-]+\/video\/\d+(\?[^ \n\r\t]*)?)", RegexOptions.IgnoreCase);
            Match match = regex.Match(text);
            return match.Success ? match.Value.Trim() : "";
        }

        /// <summary>
        /// Извлекает ссылку на Instagram из переданного текста.
        /// </summary>
        /// <param name="text">Текст, в котором ищется ссылка на Instagram.</param>
        /// <returns>Возвращает найденную ссылку Instagram или пустую строку, если ссылка не найдена.</returns>
        public static string ExtractInstagramUrl(string text)
        {
            Regex regex = new(@"https?:\/\/(www\.)?instagram\.com\/[A-Za-z0-9_\.\-\/\?\=&]+", RegexOptions.IgnoreCase);
            Match match = regex.Match(text);
            return match.Success ? match.Value.Trim() : "";
        }

        /// <summary>
        /// Извлекает ссылку на YouTube из переданного текста.
        /// </summary>
        /// <param name="text">Текст, в котором ищется ссылка на YouTube.</param>
        /// <returns>Возвращает найденную ссылку YouTube или пустую строку, если ссылка не найдена.</returns>
        public static string ExtractYouTubeUrl(string text)
        {
            // regex to match additional YouTube URL patterns:
            // 1. Standard videos: youtube.com/watch?v=ID
            // 2. Shorts: youtube.com/shorts/ID
            // 3. Community posts: youtube.com/community/, youtube.com/post/
            // 4. Channel community posts: youtube.com/channel/CHANNEL_ID/community
            // 5. Short links: youtu.be/ID
            Regex regex = new(@"https?:\/\/(www\.)?(youtube\.com\/watch\?v=[A-Za-z0-9_-]+|youtube\.com\/shorts\/[A-Za-z0-9_-]+|youtube\.com\/community\/[A-Za-z0-9_-]+|youtube\.com\/post\/[A-Za-z0-9_-]+|youtube\.com\/channel\/[A-Za-z0-9_-]+\/community|youtu\.be\/[A-Za-z0-9_-]+)([&?\/][A-Za-z0-9_=.-]+)*", RegexOptions.IgnoreCase);
            Match match = regex.Match(text);
            return match.Success ? match.Value.Trim() : "";
        }
    }
}
