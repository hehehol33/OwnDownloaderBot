using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TikTok_bot
{
    /// <summary>
    /// Класс для извлечения ссылок на платформы TikTok и Instagram из текста с использованием регулярных выражений.
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
            Regex regex = new(@"https?:\/\/(www\.)?(vm\.tiktok\.com\/[A-Za-z0-9]+\/?|tiktok\.com\/@[A-Za-z0-9_.-]+\/video\/\d+)", RegexOptions.IgnoreCase);
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
    }
}
